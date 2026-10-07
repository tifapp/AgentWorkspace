using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOS.Core;

/// <summary>Automatic effect admission shared by every managed agent-os process on this machine.</summary>
public sealed class MachineCoordinator
{
    private readonly string _root;
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "coordination");
    public MachineCoordinator(string? root = null) { _root = Path.GetFullPath(root ?? DefaultRoot); Directory.CreateDirectory(_root); }
    public async Task<IDisposable> EnterAsync(string resource, string task, Action<string>? feedback, CancellationToken cancel)
    {
        // Resource is derived by the trusted operation adapter, never supplied by the reasoning host.
        var id = Guid.NewGuid().ToString("N");
        using var owner = Process.GetCurrentProcess();
        var entry = new Admission { Id = id, Resource = resource.ToUpperInvariant(), Task = task, Pid = owner.Id, Started = owner.StartTime.ToUniversalTime().Ticks };
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
                    var blocked = state.Entries.Any(x => x.Id != id && x.Resource == entry.Resource && (x.Running || x.Sequence < current.Sequence));
                    if (!blocked)
                    {
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
    private Journal Read()
    {
        var file = Path.Combine(_root, "journal.json"); if (!File.Exists(file)) return new();
        var state = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file), JsonFormat.Options) ?? throw new InvalidDataException("The machine coordination journal is empty.");
        if (state.Schema != 1 || state.Entries.Select(x => x.Id).Distinct().Count() != state.Entries.Count || state.Entries.Any(x => x.Pid <= 0 || x.Started <= 0 || string.IsNullOrEmpty(x.Resource)))
            throw new InvalidDataException("The machine coordination journal is invalid. Admission is stopped; restore the journal from evidence.");
        return state;
    }
    private void Write(Journal state)
    {
        if (state.History.Count > 512) state.History.RemoveRange(0, state.History.Count - 512);
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
    private sealed class Journal { public int Schema { get; set; } = 1; public long Sequence { get; set; } public List<Admission> Entries { get; set; } = []; public List<AdmissionHistory> History { get; set; } = []; }
    public sealed class Admission { public string Id { get; set; } = ""; public string Resource { get; set; } = ""; public string Task { get; set; } = ""; public int Pid { get; set; } public long Started { get; set; } public long Sequence { get; set; } public bool Running { get; set; } }
    public sealed record AdmissionHistory(string Id, string Resource, string Outcome, DateTimeOffset At);
}
