using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace AgentOS.Core;
public enum ExternalEffectState { Prepared, Approved, InFlight, Completed, Unknown, Stale, Canceled, Unavailable }
public sealed record EffectScope(string Kind, string CandidateCommit, string EvidenceSha256, string ArtifactSha256, string CommandSha256, string EnvironmentSha256, string Destination, string Operation, string ParametersJson)
{
    [JsonIgnore] public string Digest => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
    public void Validate()
    {
        if (new[] { Kind, CandidateCommit, EvidenceSha256, ArtifactSha256, CommandSha256, EnvironmentSha256, Destination, Operation, ParametersJson }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Complete immutable scope required.");
        if (new[] { EvidenceSha256, ArtifactSha256, CommandSha256, EnvironmentSha256 }.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-F0-9]{64}$"))) throw new ArgumentException("Scope hashes must be uppercase SHA-256.");
        if (ParametersJson.Length > 6291456) throw new ArgumentException("Parameters too large.");
        using var _ = JsonDocument.Parse(ParametersJson);
    }
}
public sealed record EffectApproval(string ScopeDigest, string Approver, DateTimeOffset At);
public sealed record EffectIntent(string Id, EffectScope Scope, ExternalEffectState State, EffectApproval? Approval, string? RemoteOperationId, string Detail, DateTimeOffset UpdatedAt);
public sealed record EffectOutcome(ExternalEffectState State, string Detail, string? RemoteOperationId = null);
public interface IEffectAdapter
{
    Task<EffectIntent> PrepareAsync(string id, EffectScope scope, CancellationToken ct = default);
    Task<EffectIntent> ExecuteAsync(string id, CancellationToken ct = default);
    Task<EffectIntent> CancelAsync(string id, CancellationToken ct = default);
    Task<EffectIntent> ReconcileAsync(string id, CancellationToken ct = default);
}
public sealed class EffectIntentJournal
{
    private readonly string root;
    public EffectIntentJournal(string root) { this.root = Path.GetFullPath(root); Directory.CreateDirectory(this.root); }
    private string PathFor(string id) => Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    public IDisposable Lock(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Stable operation ID required.");
        for (int i = 0; i < 100; i++)
        {
            try { return new FileStream(PathFor(id) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 99) { Thread.Sleep(50); }
        }
        throw new IOException("Intent owned by another process.");
    }
    public EffectIntent? Read(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<EffectIntent>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty intent.");
        if (value.Id != id) throw new InvalidDataException("Intent identity mismatch.");
        value.Scope.Validate(); return value;
    }
    public void Save(EffectIntent value)
    {
        value.Scope.Validate(); var path = PathFor(value.Id); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, value); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public EffectIntent Approve(string id, string digest, string approver)
    {
        using var held = Lock(id); var value = Read(id) ?? throw new InvalidOperationException("Prepare first.");
        if (value.State != ExternalEffectState.Prepared || value.Scope.Digest != digest || string.IsNullOrWhiteSpace(approver)) throw new InvalidOperationException("Exact prepared scope approval required.");
        value = value with { State = ExternalEffectState.Approved, Approval = new(digest, approver, DateTimeOffset.UtcNow), UpdatedAt = DateTimeOffset.UtcNow };
        Save(value); return value;
    }
}
public abstract class EffectAdapterBase(EffectIntentJournal journal, string kind) : IEffectAdapter
{
    protected EffectIntentJournal Journal => journal;
    public Task<EffectIntent> PrepareAsync(string id, EffectScope scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); scope.Validate(); if (scope.Kind != kind) throw new ArgumentException("Wrong effect kind.");
        using var held = journal.Lock(id); var old = journal.Read(id);
        if (old != null) { if (old.Scope != scope) throw new InvalidOperationException("Operation ID bound to another scope."); return Task.FromResult(old); }
        var value = new EffectIntent(id, scope, ExternalEffectState.Prepared, null, null, "Prepared.", DateTimeOffset.UtcNow);
        journal.Save(value); return Task.FromResult(value);
    }
    public async Task<EffectIntent> ExecuteAsync(string id, CancellationToken ct = default)
    {
        using var held = journal.Lock(id); var value = journal.Read(id) ?? throw new InvalidOperationException("Prepare first.");
        if (value.Scope.Kind != kind) throw new InvalidOperationException("Wrong effect kind.");
        if (value.State is ExternalEffectState.InFlight or ExternalEffectState.Unknown) return await CheckLocked(value, ct);
        if (value.State != ExternalEffectState.Approved || value.Approval?.ScopeDigest != value.Scope.Digest) return value;
        ct.ThrowIfCancellationRequested();
        value = value with { State = ExternalEffectState.InFlight, Detail = "External outcome pending.", UpdatedAt = DateTimeOffset.UtcNow };
        journal.Save(value);
        try { return Save(value, await ApplyAsync(value, ct)); }
        catch (Exception e) { return Save(value, new(ExternalEffectState.Unknown, "Outcome unknown: " + e.GetType().Name)); }
    }
    public Task<EffectIntent> CancelAsync(string id, CancellationToken ct = default)
    {
        using var held = journal.Lock(id); var value = journal.Read(id) ?? throw new InvalidOperationException("Unknown intent.");
        if (value.State is ExternalEffectState.Prepared or ExternalEffectState.Approved) { value = value with { State = ExternalEffectState.Canceled, Detail = "Canceled before dispatch.", UpdatedAt = DateTimeOffset.UtcNow }; journal.Save(value); }
        return Task.FromResult(value);
    }
    public async Task<EffectIntent> ReconcileAsync(string id, CancellationToken ct = default)
    { using var held = journal.Lock(id); return await CheckLocked(journal.Read(id) ?? throw new InvalidOperationException("Unknown intent."), ct); }
    private async Task<EffectIntent> CheckLocked(EffectIntent value, CancellationToken ct)
    {
        if (value.State is not (ExternalEffectState.InFlight or ExternalEffectState.Unknown)) return value;
        try { return Save(value, await CheckAsync(value, ct)); }
        catch (Exception e) { return Save(value, new(ExternalEffectState.Unknown, "Reconciliation unavailable: " + e.GetType().Name)); }
    }
    private EffectIntent Save(EffectIntent value, EffectOutcome result)
    { value = value with { State = result.State, Detail = result.Detail, RemoteOperationId = result.RemoteOperationId ?? value.RemoteOperationId, UpdatedAt = DateTimeOffset.UtcNow }; journal.Save(value); return value; }
    protected abstract Task<EffectOutcome> ApplyAsync(EffectIntent value, CancellationToken ct);
    protected abstract Task<EffectOutcome> CheckAsync(EffectIntent value, CancellationToken ct);
}
public interface IScopedCredentialProvider { string? GetSecret(string target); }
public sealed class WindowsCredentialManagerProvider : IScopedCredentialProvider
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Credential
    {
        public uint Flags, Type; public string TargetName, Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount; public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredFree")] private static extern void CredFree(IntPtr credential);
    public string? GetSecret(string target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!(target.StartsWith("AgentOS/GitHub/", StringComparison.Ordinal) || target.StartsWith("AgentOS/PostgreSQL/", StringComparison.Ordinal) || target.StartsWith("AgentOS/Deployment/", StringComparison.Ordinal)) || target.Length > 256) throw new ArgumentException("Unscoped credential target.");
        if (!CredRead(target, 1, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            if (c.Persist < 2 || c.CredentialBlobSize is 0 or > 8192) return null;
            var bytes = new byte[c.CredentialBlobSize]; Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(ptr); }
    }
}
public sealed record GitHubEffect(string ApiOrigin, string Owner, string Repository, string Branch, string Commit, string? ExpectedOldCommit, string? BaseBranch, string? PullTitle, string? PullBody, GitObjectSnapshot? Snapshot = null, string? ExpectedBaseCommit = null);
public sealed class GitHubEffects : EffectAdapterBase
{
    private readonly HttpClient client; private readonly IScopedCredentialProvider credentials; private readonly HashSet<string> origins;
    public GitHubEffects(EffectIntentJournal journal, IScopedCredentialProvider credentials, HttpMessageHandler? transport = null, IEnumerable<string>? enterpriseOrigins = null) : base(journal, "github")
    {
        if (transport is HttpClientHandler handler && handler.AllowAutoRedirect) throw new ArgumentException("Redirects must be disabled.");
        client = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false }); this.credentials = credentials; origins = new(StringComparer.OrdinalIgnoreCase) { "https://api.github.com" };
        foreach (var origin in enterpriseOrigins ?? []) origins.Add(Origin(origin));
        client.Timeout = TimeSpan.FromSeconds(30);
    }
    private static string Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("HTTPS API authority required.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private GitHubEffect Spec(EffectIntent value)
    {
        var s = JsonSerializer.Deserialize<GitHubEffect>(value.Scope.ParametersJson) ?? throw new InvalidDataException("GitHub parameters missing.");
        if (!origins.Contains(Origin(s.ApiOrigin)) || !s.Commit.Equals(value.Scope.CandidateCommit, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(s.Commit, "^[a-fA-F0-9]{40}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(s.Owner, "^[A-Za-z0-9-]{1,39}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(s.Repository, "^[A-Za-z0-9_.-]{1,100}$") ||
            s.Branch.StartsWith('-') || s.Branch.Contains("..") || s.Branch.Contains("@{") ||
            !System.Text.RegularExpressions.Regex.IsMatch(s.Branch, "^[A-Za-z0-9_./-]{1,200}$") ||
            value.Scope.Destination != s.ApiOrigin + "/" + s.Owner + "/" + s.Repository || s.BaseBranch == null || !System.Text.RegularExpressions.Regex.IsMatch(s.BaseBranch,"^[A-Za-z0-9_./-]{1,200}$") || s.BaseBranch.Contains("..") || s.BaseBranch.Contains("@{") || (value.Scope.Operation == "pull_request" && (s.ExpectedBaseCommit == null || !System.Text.RegularExpressions.Regex.IsMatch(s.ExpectedBaseCommit,"^[a-fA-F0-9]{40}$"))))
            throw new InvalidDataException("GitHub scope mismatch.");
        s.Snapshot?.Validate(s.Commit); return s;
    }
    private async Task<(HttpStatusCode, JsonDocument?)> Send(GitHubEffect s, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var origin = Origin(s.ApiOrigin);
        using var req = new HttpRequestMessage(method, origin + path);
        var requestedUri = req.RequestUri;
        req.Headers.UserAgent.ParseAdd("AgentOS/1.0"); req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var secret = credentials.GetSecret("AgentOS/GitHub/" + new Uri(origin).Authority + "/" + s.Owner + "/" + s.Repository);
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Credential unavailable.");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        // Injected clients must disable automatic redirects. A redirect endpoint is never approved.
        if (response.RequestMessage?.RequestUri != requestedUri || (int)response.StatusCode is >= 300 and < 400) throw new InvalidOperationException("GitHub redirect rejected.");
        if (response.Content.Headers.ContentLength > 6291456) throw new InvalidDataException("Response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = await input.ReadAsync(buffer, ct)) != 0) { if (bytes.Length + n > 6291456) throw new InvalidDataException("Response too large."); bytes.Write(buffer, 0, n); }
        bytes.Position = 0; return (response.StatusCode, bytes.Length == 0 ? null : JsonDocument.Parse(bytes));
    }
    private static string E(string s) => Uri.EscapeDataString(s);
    private static string Repo(GitHubEffect s) => "/repos/" + E(s.Owner) + "/" + E(s.Repository);
    private Task<bool> CommitMatches(GitHubEffect s,CancellationToken ct) => CommitMatches(s,s.Snapshot?.Commit,ct);
    private async Task<bool> CommitMatches(GitHubEffect s,GitCommitSnapshot? expected,CancellationToken ct)
    {
        var sha=expected?.Sha??s.Commit;
        var(status,doc)=await Send(s,HttpMethod.Get,Repo(s)+"/git/commits/"+sha,null,ct);
        using(doc){if(status==HttpStatusCode.NotFound)return false;if(status!=HttpStatusCode.OK||doc==null)throw new InvalidDataException("Commit lookup inconclusive.");
            if(!string.Equals(doc.RootElement.GetProperty("sha").GetString(),sha,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Commit SHA mismatch.");
            return expected==null||doc.RootElement.GetProperty("tree").GetProperty("sha").GetString()==expected.Tree&&doc.RootElement.GetProperty("parents").EnumerateArray().Select(x=>x.GetProperty("sha").GetString()).SequenceEqual(expected.Parents);}
    }
    private async Task<bool> BaseMatches(GitHubEffect s,CancellationToken ct)
    {
        if(s.BaseBranch==null||s.ExpectedBaseCommit==null)return false;
        var(status,doc)=await Send(s,HttpMethod.Get,Repo(s)+"/git/ref/heads/"+string.Join('/',s.BaseBranch.Split('/').Select(E)),null,ct);
        using(doc){if(status!=HttpStatusCode.OK||doc==null)throw new InvalidDataException("PR base lookup inconclusive.");return string.Equals(doc.RootElement.GetProperty("object").GetProperty("sha").GetString(),s.ExpectedBaseCommit,StringComparison.OrdinalIgnoreCase);}
    }
    private async Task<bool> Exists(GitHubEffect s,string type,string sha,CancellationToken ct)
    {
        var(status,doc)=await Send(s,HttpMethod.Get,Repo(s)+"/git/"+type+"/"+sha,null,ct);
        using(doc){if(status==HttpStatusCode.NotFound)return false;if(status!=HttpStatusCode.OK||doc==null||!string.Equals(doc.RootElement.GetProperty("sha").GetString(),sha,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Git object lookup inconclusive.");return true;}
    }
    private async Task Upload(GitHubEffect s,CancellationToken ct)
    {
        var snapshot=s.Snapshot??throw new InvalidDataException("Approved Git object snapshot required.");snapshot.Validate(s.Commit);
        var ancestors=snapshot.Ancestors??[];var first=ancestors.Length>0?ancestors[0]:snapshot.Commit;
        foreach(var boundary in first.Parents)if(!await Exists(s,"commits",boundary,ct))throw new InvalidDataException("Approved Git history bound reached before a remote ancestor; no ref changed.");
        foreach(var obj in snapshot.Objects){ct.ThrowIfCancellationRequested();var plural=obj.Type=="blob"?"blobs":"trees";if(await Exists(s,plural,obj.Sha,ct))continue;
            object body=obj.Type=="blob"?new{content=obj.Base64,encoding="base64"}:new{tree=obj.Entries!.Select(e=>new{path=e.Path,mode=e.Mode=="40000"?"040000":e.Mode,type=e.Type,sha=e.Sha}).ToArray()};
            var(status,doc)=await Send(s,HttpMethod.Post,Repo(s)+"/git/"+plural,body,ct);using(doc){if(status!=HttpStatusCode.Created||doc==null||!string.Equals(doc.RootElement.GetProperty("sha").GetString(),obj.Sha,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Git Data API returned different object SHA.");}}
        foreach(var c in ancestors.Append(snapshot.Commit))
        {
            if(await CommitMatches(s,c,ct))continue;
            var(created,response)=await Send(s,HttpMethod.Post,Repo(s)+"/git/commits",new{message=c.Message,tree=c.Tree,parents=c.Parents,author=new{name=c.AuthorName,email=c.AuthorEmail,date=c.AuthorDate},committer=new{name=c.CommitterName,email=c.CommitterEmail,date=c.CommitterDate}},ct);
            using(response){if(created!=HttpStatusCode.Created||response==null||!string.Equals(response.RootElement.GetProperty("sha").GetString(),c.Sha,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Git Data API cannot reproduce exact approved commit SHA.");}
            if(!await CommitMatches(s,c,ct))throw new InvalidDataException("Uploaded commit tree or parents differ.");
        }
    }
    private async Task<EffectOutcome> Branch(GitHubEffect s, CancellationToken ct)
    {
        var (status, doc) = await Send(s, HttpMethod.Get, Repo(s) + "/git/ref/heads/" + string.Join('/', s.Branch.Split('/').Select(E)), null, ct);
        using (doc)
        {
            if (status == HttpStatusCode.NotFound) return new(ExternalEffectState.Prepared, "Branch absent.");
            if (status != HttpStatusCode.OK || doc == null) return new(ExternalEffectState.Unknown, "Branch lookup inconclusive.");
            var actual = doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
            if(!string.Equals(actual,s.Commit,StringComparison.OrdinalIgnoreCase))return new(ExternalEffectState.Stale,"Branch points to another commit."); return s.Snapshot==null||await CommitMatches(s,ct)?new(ExternalEffectState.Completed,"Exact branch commit confirmed.",s.Branch):new(ExternalEffectState.Stale,"Branch commit tree or parents differ.");
        }
    }
    private async Task<EffectOutcome> Pull(GitHubEffect s, CancellationToken ct)
    {
        var (status, doc) = await Send(s, HttpMethod.Get, Repo(s) + "/pulls?state=all&head=" + E(s.Owner + ":" + s.Branch) + "&base=" + E(s.BaseBranch ?? "") + "&per_page=100", null, ct);
        using (doc)
        {
            if (status != HttpStatusCode.OK || doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return new(ExternalEffectState.Unknown, "PR lookup inconclusive.");
            if (doc.RootElement.GetArrayLength() == 100) return new(ExternalEffectState.Unknown, "PR results truncated; no create attempted.");
            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                if (!string.Equals(pr.GetProperty("head").GetProperty("sha").GetString(), s.Commit, StringComparison.OrdinalIgnoreCase) || pr.GetProperty("state").GetString() != "open" || !string.Equals(pr.GetProperty("base").GetProperty("sha").GetString(),s.ExpectedBaseCommit,StringComparison.OrdinalIgnoreCase) || pr.GetProperty("title").GetString() != s.PullTitle || (pr.GetProperty("body").GetString() ?? "") != (s.PullBody ?? "")) return new(ExternalEffectState.Stale, "Existing PR differs from approved scope.");
                return new(ExternalEffectState.Completed, "Exact PR confirmed.", pr.GetProperty("number").GetInt32().ToString());
            }
            return new(ExternalEffectState.Prepared, "No matching PR.");
        }
    }
    protected override async Task<EffectOutcome> ApplyAsync(EffectIntent value, CancellationToken ct)
    {
        var s = Spec(value);
        if (value.Scope.Operation == "branch")
        {
            var observed = await Branch(s, ct);
            if (observed.State == ExternalEffectState.Completed) return observed;
            if (observed.State == ExternalEffectState.Stale) return new(ExternalEffectState.Stale, "Existing ref differs; REST has no atomic expected-old guard.");
            if (observed.State != ExternalEffectState.Prepared) return observed;
            if(s.ExpectedOldCommit!=null)return new(ExternalEffectState.Stale,"Only expected-absent ref creation supported.");
            await Upload(s,ct);
            var (status, doc) = await Send(s, HttpMethod.Post, Repo(s) + "/git/refs", new { @ref = "refs/heads/" + s.Branch, sha = s.Commit }, ct);
            using (doc) { var result = await Branch(s, ct); return result.State == ExternalEffectState.Prepared ? new(ExternalEffectState.Unknown, "Branch creation unconfirmed; no automatic retry.") : result; }
        }
        if (value.Scope.Operation == "pull_request")
        {
            if (string.IsNullOrWhiteSpace(s.BaseBranch) || string.IsNullOrWhiteSpace(s.PullTitle)) return new(ExternalEffectState.Stale, "PR base and title required.");
            if(!await BaseMatches(s,ct))return new(ExternalEffectState.Stale,"PR base commit changed.");
            var branch = await Branch(s, ct); if (branch.State != ExternalEffectState.Completed) return branch;
            var existing = await Pull(s, ct); if (existing.State != ExternalEffectState.Prepared) return existing;
            if(!await BaseMatches(s,ct))return new(ExternalEffectState.Stale,"PR base commit changed before creation.");
            var headAgain=await Branch(s,ct);if(headAgain.State!=ExternalEffectState.Completed)return headAgain;
            var (status, doc) = await Send(s, HttpMethod.Post, Repo(s) + "/pulls", new { head = s.Branch, @base = s.BaseBranch, title = s.PullTitle, body = s.PullBody ?? "" }, ct);
            using (doc) { var result = await Pull(s, ct); return result.State == ExternalEffectState.Prepared ? new(ExternalEffectState.Unknown, "PR creation unconfirmed; no automatic retry.") : result; }
        }
        return new(ExternalEffectState.Stale, "Unsupported operation.");
    }
    protected override Task<EffectOutcome> CheckAsync(EffectIntent value, CancellationToken ct)
    { var s = Spec(value); return CheckRemote(s, value.Scope.Operation, ct); }
    private async Task<EffectOutcome> CheckRemote(GitHubEffect s, string operation, CancellationToken ct)
    { var result = operation == "branch" ? await Branch(s, ct) : await Pull(s, ct); return result.State is ExternalEffectState.Prepared or ExternalEffectState.Stale ? new(ExternalEffectState.Unknown, "Historical remote outcome requires manual review; no retry.") : result; }
}
public sealed record DeploymentExecutionSnapshot(HyperVProfile Profile, DeploymentEffectSettings Settings);
public sealed record DeploymentEffect(string Backend, string ArtifactSha256, string CommandSha256, string EnvironmentSha256, string Destination, DeploymentReceiptContract? Receipt = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeploymentExecutionSnapshot? Execution = null);
public sealed record DeploymentCapability(string Backend, string IsolationKind, string Destination, bool Verified);
public interface IIsolatedDeploymentExecutor
{
    DeploymentCapability Capability { get; }
    Task<EffectOutcome> ExecuteAsync(string operationId, DeploymentEffect effect, CancellationToken ct);
    Task<EffectOutcome> ReconcileAsync(string operationId, DeploymentEffect effect, CancellationToken ct);
}
public sealed class DeploymentEffects(EffectIntentJournal journal, IIsolatedDeploymentExecutor? executor, DeploymentReceiptProbe? receipts = null) : EffectAdapterBase(journal, "deployment")
{
    private DeploymentEffect Spec(EffectIntent value)
    {
        var s = JsonSerializer.Deserialize<DeploymentEffect>(value.Scope.ParametersJson) ?? throw new InvalidDataException("Deployment parameters missing.");
        if (s.ArtifactSha256 != value.Scope.ArtifactSha256 || s.CommandSha256 != value.Scope.CommandSha256 || s.EnvironmentSha256 != value.Scope.EnvironmentSha256 || s.Destination != value.Scope.Destination) throw new InvalidDataException("Deployment scope mismatch.");
        return s;
    }
    protected override Task<EffectOutcome> ApplyAsync(EffectIntent value, CancellationToken ct)
    {
        var s = Spec(value);
        if (executor == null || !executor.Capability.Verified || executor.Capability.IsolationKind != "vm" || executor.Capability.Backend != s.Backend || executor.Capability.Destination != s.Destination)
            return Task.FromResult(new EffectOutcome(ExternalEffectState.Unavailable, "Isolated VM executor unavailable; confinement unproven."));
        return ApplyWithReceipt(value,s,ct);
    }
    private async Task<EffectOutcome> ApplyWithReceipt(EffectIntent value,DeploymentEffect s,CancellationToken ct)
    {
        if(s.Receipt==null||receipts==null)return new(ExternalEffectState.Unavailable,"Remote applied receipt contract unavailable.");
        DeploymentReceiptProbe.Validate(s.Receipt);
        var result=await executor!.ExecuteAsync(value.Id,s,ct);
        if(result.State!=ExternalEffectState.Completed)return result.State is ExternalEffectState.Stale or ExternalEffectState.Unavailable?result:new(ExternalEffectState.Unknown,"VM execution or shutdown unconfirmed: "+result.Detail);
        return await receipts.CheckAsync(value.Id,value.Scope,s.Receipt,ct);
    }
    protected override async Task<EffectOutcome> CheckAsync(EffectIntent value, CancellationToken ct)
    {
        var s = Spec(value);
        if(executor==null||!executor.Capability.Verified||executor.Capability.IsolationKind!="vm"||executor.Capability.Backend!=s.Backend||executor.Capability.Destination!=s.Destination)return new(ExternalEffectState.Unknown,"Frozen VM executor unavailable; owned shutdown unconfirmed.");
        var vm=await executor.ReconcileAsync(value.Id,s,ct);
        if(vm.State!=ExternalEffectState.Completed)return new(ExternalEffectState.Unknown,"Owned VM outcome unconfirmed: "+vm.Detail);
        if(s.Receipt==null||receipts==null)return new(ExternalEffectState.Unknown,"Remote applied receipt contract unavailable.");
        var receipt=await receipts.CheckAsync(value.Id,value.Scope,s.Receipt,ct);
        return receipt.State==ExternalEffectState.Completed?receipt:new(ExternalEffectState.Unknown,"Owned VM confirmed; remote applied receipt unconfirmed: "+receipt.Detail);
    }
}












