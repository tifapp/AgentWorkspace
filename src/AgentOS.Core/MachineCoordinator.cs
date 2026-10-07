using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOS.Core;

/// <summary>Automatic effect admission shared by every managed agent-os process on this machine.</summary>
public sealed class MachineCoordinator
{
    private readonly string _root;
    private static readonly Lazy<string> AssemblyIdentity = new(() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MachineCoordinator).Assembly.Location))));
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "coordination");
    public MachineCoordinator(string? root = null) { _root = Path.GetFullPath(root ?? DefaultRoot); Directory.CreateDirectory(_root); }
    public string Root => _root;
    public const int Protocol = 2;
    public IDisposable RegisterRuntime()
    {
        using var gate = Lock(CancellationToken.None).GetAwaiter().GetResult();
        Read();
        using var process = Process.GetCurrentProcess();
        var owner = new RuntimeOwner { Id = Guid.NewGuid().ToString("N"), Pid = process.Id, Started = process.StartTime.ToUniversalTime().Ticks,
            Protocol = Protocol, Assembly = AssemblyIdentity.Value, OpenedAtUtc = DateTimeOffset.UtcNow };
        var path = Path.Combine(_root, "runtime-" + owner.Id + ".json");
        SaveOwner(path, owner);
        return new RuntimeLease(this, path, owner);
    }
    private static void SaveOwner(string path, RuntimeOwner owner)
    {
        var next = path + ".next";
        using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, owner, JsonFormat.Options); stream.Flush(true); }
        File.Move(next, path, true);
    }
    private sealed class RuntimeLease(MachineCoordinator coordinator, string path, RuntimeOwner owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            using var gate = coordinator.Lock(CancellationToken.None).GetAwaiter().GetResult();
            owner.ClosedAtUtc = DateTimeOffset.UtcNow;
            SaveOwner(path, owner);
        }
    }
    private sealed class RuntimeOwner
    {
        public string Id { get; set; } = "";
        public int Pid { get; set; }
        public long Started { get; set; }
        public int Protocol { get; set; }
        public string Assembly { get; set; } = "";
        public DateTimeOffset OpenedAtUtc { get; set; }
        public DateTimeOffset? ClosedAtUtc { get; set; }
    }
    public Task<IDisposable> EnterAsync(string resource, string task, Action<string>? feedback, CancellationToken cancel) => EnterAsync(new[] { new ResourceClaim("legacy", resource.ToUpperInvariant(), ResourceAccess.Write) }, task, feedback, cancel);
    public async Task<IDisposable> EnterAsync(IEnumerable<ResourceClaim> resources, string task, Action<string>? feedback, CancellationToken cancel)
    {
        // Resource is derived by the trusted operation adapter, never supplied by the reasoning host.
        var claims = resources.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        if (claims.Count == 0 || claims.Any(x => string.IsNullOrWhiteSpace(x.Kind) || string.IsNullOrWhiteSpace(x.Key))) throw new ArgumentException("Resource claims are required.");
        if (claims.Any(x => claims.Any(y => !ReferenceEquals(x, y) && ResourceAdmission.Conflicts(x, y)))) throw new ArgumentException("Overlapping claims must be coalesced by the adapter.");
        var id = Guid.NewGuid().ToString("N");
        using var owner = Process.GetCurrentProcess();
        var entry = new Admission { Id = id, Resource = string.Join(";", claims.Select(x => x.Key)), Claims = claims, Task = task, QueuedAtUtc = DateTimeOffset.UtcNow, Pid = owner.Id, Started = owner.StartTime.ToUniversalTime().Ticks };
        var added = false; var reported = false;
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                using (await Lock(cancel))
                {
                    var state = Read(); Recover(state);
                    if (!added) { entry.Sequence = ++state.Sequence; state.Entries.Add(entry); added = true; }
                    var current = state.Entries.Single(x => x.Id == id);
                    var blocked = state.Entries.Any(x => x.Id != id && x.Claims.Any(a => entry.Claims.Any(b => ResourceAdmission.Conflicts(a, b))) && (x.Running || x.Sequence < current.Sequence));
                    if (!blocked)
                    {
                        foreach (var claim in current.Claims) ResourceAdmission.Revalidate(claim);
                        current.Running = true; Write(state);
                        if (reported) feedback?.Invoke("The shared operation is available. Its current preconditions will be checked again.");
                        return new Ownership(this, id);
                    }
                    Write(state);
                }
                if (!reported) { feedback?.Invoke("Waiting for another managed operation on the same shared resource. Independent work can continue."); reported = true; }
                await Task.Delay(50, cancel);
            }
        }
        catch { if (added) await Remove(id, "Canceled before admission"); throw; }
    }
    public IReadOnlyList<Admission> Snapshot()
    { using var gate = Lock(CancellationToken.None).GetAwaiter().GetResult(); var state = Read(); Recover(state); Write(state); return state.Entries; }
    public IReadOnlyList<AdmissionDiagnosis> Diagnose()
    {
        var entries = Snapshot();
        return entries.Where(x => !x.Running).Select(x => new AdmissionDiagnosis(x.Id, x.Sequence, DateTimeOffset.UtcNow - x.QueuedAtUtc, entries.Where(y => y.Id != x.Id && (y.Running || y.Sequence < x.Sequence) && y.Claims.Any(a => x.Claims.Any(b => ResourceAdmission.Conflicts(a, b)))).Select(y => y.Id).ToArray(), false, DateTimeOffset.UtcNow - x.QueuedAtUtc > TimeSpan.FromMinutes(5))).ToArray();
    }
    public sealed record AdmissionDiagnosis(string Id, long Sequence, TimeSpan Waited, IReadOnlyList<string> BlockingIds, bool Cycle, bool Starved);
    private async Task<FileStream> Lock(CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(_root, "authority.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, timeout.Token); }
        }
    }
    private static bool IsAlive(int pid, long started)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private IEnumerable<RuntimeOwner> RegisteredOwners()
    {
        foreach (var path in Directory.GetFiles(_root, "runtime-*.json"))
        {
            var owner = JsonSerializer.Deserialize<RuntimeOwner>(File.ReadAllText(path), JsonFormat.Options)
                ?? throw new InvalidDataException("Runtime registration is invalid.");
            if (owner.Pid <= 0 || owner.Started <= 0 || owner.Protocol <= 0 || string.IsNullOrWhiteSpace(owner.Assembly))
                throw new InvalidDataException("Runtime registration identity is incomplete.");
            yield return owner;
        }
    }
    public void UpgradeSchema1Offline(bool legacyRuntimeLocksDrained)
    {
        if (!legacyRuntimeLocksDrained) throw new InvalidOperationException("Close legacy runtimes and drain all owning project locks before offline upgrade.");
        using var gate = Lock(CancellationToken.None).GetAwaiter().GetResult();
        var file = Path.Combine(_root, "journal.json");
        if (!File.Exists(file)) throw new InvalidOperationException("There is no schema 1 journal to upgrade.");
        var state = ReadRaw();
        if (state.Schema == 2) return;
        if (state.Entries.Any(x => IsAlive(x.Pid, x.Started)))
            throw new InvalidOperationException("Legacy owner is live. Close old runtimes and drain owning project locks before offline upgrade.");
        if (RegisteredOwners().Any(x => x.ClosedAtUtc == null && IsAlive(x.Pid, x.Started)))
            throw new InvalidOperationException("Registered runtime owner is live. Close it before offline upgrade.");
        var backup = Path.Combine(_root, "journal.schema1.backup.json");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(file));
        if (!File.Exists(backup))
        {
            var next = backup + ".next";
            using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { source.CopyTo(copy); copy.Flush(true); }
            if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(next)))) throw new IOException("Journal migration backup verification failed.");
            File.Move(next, backup);
        }
        else if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(backup))))
            throw new InvalidDataException("Existing schema 1 backup differs from the journal.");
        foreach (var item in state.Entries) item.Claims = [new ResourceClaim("legacy", item.Resource, ResourceAccess.Write)];
        state.Schema = 2;
        Write(state);
    }
    private Journal Read()
    {
        var state = ReadRaw();
        if (state.Schema == 1)
            throw new InvalidOperationException("Schema 1 requires explicit offline upgrade after all old runtimes and owning project locks are drained. Journal and receipts are unchanged.");
        var assembly = AssemblyIdentity.Value;
        if (RegisteredOwners().Any(x => x.ClosedAtUtc == null && IsAlive(x.Pid, x.Started) &&
            (x.Protocol != Protocol || !string.Equals(x.Assembly, assembly, StringComparison.Ordinal))))
            throw new InvalidOperationException("An incompatible live runtime owner is registered. Close it before using shared coordination.");
        return state;
    }
    private Journal ReadRaw()
    {
        var file = Path.Combine(_root, "journal.json"); if (!File.Exists(file)) return new();
        var state = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file), JsonFormat.Options) ?? throw new InvalidDataException("The machine coordination journal is empty.");
        if ((state.Schema != 1 && state.Schema != 2) || state.Entries.Select(x => x.Id).Distinct().Count() != state.Entries.Count || state.Entries.Any(x => x.Pid <= 0 || x.Started <= 0 || string.IsNullOrEmpty(x.Resource)))
            throw new InvalidDataException("The machine coordination journal is invalid. Admission is stopped; restore the journal from evidence.");
        if (state.Schema == 2 && state.Entries.Any(x => x.Claims == null || x.Claims.Count == 0))
            throw new InvalidDataException("Admission claims missing.");
        return state;
    }
    private void Write(Journal state)
    {
        var temp = Path.Combine(_root, "journal.next");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, state, JsonFormat.Options); stream.Flush(true); }
        File.Move(temp, Path.Combine(_root, "journal.json"), true);
    }
    private static void Recover(Journal state)
    {
        foreach (var entry in state.Entries.ToArray())
        {
            bool alive;
            try { using var owner = Process.GetProcessById(entry.Pid); alive = !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == entry.Started; }
            catch (ArgumentException) { alive = false; }
            catch (System.ComponentModel.Win32Exception) { continue; } // Unknown ownership never grants admission.
            if (!alive) { state.Entries.Remove(entry); state.History.Add(new(entry.Id, entry.Resource, "Unknown after owner exit; no effect replayed", DateTimeOffset.UtcNow)); }
        }
    }
    private async Task Remove(string id, string outcome)
    {
        using var gate = await Lock(CancellationToken.None); var state = Read(); var entry = state.Entries.SingleOrDefault(x => x.Id == id);
        if (entry != null) { state.Entries.Remove(entry); state.History.Add(new(id, entry.Resource, outcome, DateTimeOffset.UtcNow)); Write(state); }
    }
    private sealed class Ownership(MachineCoordinator coordinator, string id) : IDisposable
    { private int _disposed; public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) coordinator.Remove(id, "Operation ownership released; consult project evidence for actual outcome").GetAwaiter().GetResult(); } }
    private sealed class Journal { public int Schema { get; set; } = 2; public long Sequence { get; set; } public List<Admission> Entries { get; set; } = []; public List<AdmissionHistory> History { get; set; } = []; }
    public sealed class Admission { public string Id { get; set; } = ""; public string Resource { get; set; } = ""; public List<ResourceClaim> Claims { get; set; } = []; public string Task { get; set; } = ""; public DateTimeOffset QueuedAtUtc { get; set; } public int Pid { get; set; } public long Started { get; set; } public long Sequence { get; set; } public bool Running { get; set; } }
    public sealed record AdmissionHistory(string Id, string Resource, string Outcome, DateTimeOffset At);
}







