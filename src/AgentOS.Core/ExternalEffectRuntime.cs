using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
public sealed record GitHubEffectSettings(string ApiOrigin,string Owner,string Repository,string Branch,string BaseBranch);
public sealed record PostgreSqlEffectSettings(string Host,int Port,string Database,string Schema,string User);
public sealed record DeploymentEffectSettings(string Backend,string Destination,string ArtifactPath,string Command,string EnvironmentName);
public sealed record ExternalEffectSettings(GitHubEffectSettings? GitHub=null,PostgreSqlEffectSettings? PostgreSql=null,DeploymentEffectSettings? Deployment=null);
public sealed record ExternalEffectRequest(string WorkId,string Provider,string Operation,string? SourcePath=null,string? Title=null,string? Body=null);
public sealed partial class ProjectRuntime
{
 string SettingsFile=>Path.Combine(_store.Root,"external-effects-settings.json");
 EffectIntentJournal Journal=>new(Path.Combine(_store.Root,"external-effects"));
 public void ConfigureExecutionProfile(ExecutionProfile profile)=>new ExecutionProfileRegistry(_store.Root).Configure(profile);
 public ExecutionProfileDescription DescribeExecutionProfile()=>new ExecutionProfileRegistry(_store.Root).Describe();
 public ExternalEffectSettings ExternalSettings=>File.Exists(SettingsFile)?JsonSerializer.Deserialize<ExternalEffectSettings>(File.ReadAllText(SettingsFile))??new():new();
 public void ConfigureExternalEffects(ExternalEffectSettings settings)
 {
  ObjectDisposedException.ThrowIf(_disposed,this);
  if(settings.GitHub is { } g && (!Uri.TryCreate(g.ApiOrigin,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.AbsolutePath!="/"||uri.Query.Length!=0||uri.Fragment.Length!=0||!Regex.IsMatch(g.Owner,"^[A-Za-z0-9-]{1,39}$")||!Regex.IsMatch(g.Repository,"^[A-Za-z0-9_.-]{1,100}$")||!Regex.IsMatch(g.Branch,"^[A-Za-z0-9_./-]{1,200}$")||g.Branch.StartsWith('-')||g.Branch.Contains("..")||g.Branch.Contains("@{")||string.IsNullOrWhiteSpace(g.BaseBranch)))throw new ArgumentException("Invalid GitHub settings.");
  if(settings.PostgreSql is { } p && (string.IsNullOrWhiteSpace(p.Host)||p.Host.Any(c=>!(char.IsLetterOrDigit(c)||c is '.' or '-'))||p.Port is <1 or >65535||!Regex.IsMatch(p.Database,"^[A-Za-z_][A-Za-z0-9_-]{0,62}$")||!Regex.IsMatch(p.Schema,"^[A-Za-z_][A-Za-z0-9_]{0,62}$")||!Regex.IsMatch(p.User,"^[A-Za-z_][A-Za-z0-9_-]{0,62}$")))throw new ArgumentException("Invalid PostgreSQL settings.");
  if(settings.Deployment is { } d && (new[]{d.Backend,d.Destination,d.ArtifactPath,d.Command,d.EnvironmentName}.Any(string.IsNullOrWhiteSpace)||d.Command.Length>8192))throw new ArgumentException("Invalid deployment settings.");
  Directory.CreateDirectory(_store.Root);var tmp=SettingsFile+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{File.WriteAllText(tmp,JsonSerializer.Serialize(settings));File.Move(tmp,SettingsFile,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}
 }
 public IReadOnlyList<EffectIntent> ListExternalEffects(){var dir=Path.Combine(_store.Root,"external-effects");return Directory.Exists(dir)?Directory.EnumerateFiles(dir,"*.json").Select(x=>JsonSerializer.Deserialize<EffectIntent>(File.ReadAllText(x))!).OrderByDescending(x=>x.UpdatedAt).ToArray():[];}
 public EffectIntent? InspectExternalEffect(string id)=>Journal.Read(id);
 static string H(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
 static string PathArg(string path){if(string.IsNullOrWhiteSpace(path)||Path.IsPathRooted(path)||path.Contains('\\')||path.Split('/').Any(x=>x is "" or "." or "..")||path.StartsWith('-'))throw new ArgumentException("Tracked relative Git path required.");return path;}
 async Task<string> Blob(string commit,string path)
 {
  path=PathArg(path);var entry=(await Commands.Git(_state.ProjectPath,"ls-tree",commit,"--",path)).Checked();
  if(!entry.StartsWith("100644 blob ")&&!entry.StartsWith("100755 blob "))throw new InvalidOperationException("Regular tracked Git blob required.");
  var oid=entry.Split(' ','\t',StringSplitOptions.RemoveEmptyEntries).ElementAt(2);var size=long.Parse((await Commands.Git(_state.ProjectPath,"cat-file","-s",oid)).Checked());
  if(size is <1 or >1048576)throw new InvalidOperationException("Git blob size unavailable.");
  var data=(await Commands.Git(_state.ProjectPath,"cat-file","blob",oid)).Output;
  if(Encoding.UTF8.GetByteCount(data)!=size||(await Commands.RunAsync("git",["hash-object","--stdin"],_state.ProjectPath,input:data)).Checked()!=oid)throw new InvalidOperationException("Git blob cannot be read byte exactly.");
  return data;
 }
 async Task<(WorkUnit,ValidationEvidence,string)> Verified(string id,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();var w=Find(id);var commit=w.IntegratedCommit;
  if(w.Status!=WorkStatus.Completed||commit==null||!Regex.IsMatch(commit,"^[a-fA-F0-9]{40}$"))throw new InvalidOperationException("Completed integrated work required.");
  var e=w.Evidence.LastOrDefault(x=>x.Commit==commit&&x.Passed)??throw new InvalidOperationException("Passing source unchanged validation required.");
  var root=Path.GetFullPath(Path.Combine(_store.Root,"evidence"))+Path.DirectorySeparatorChar;
  if(!Path.GetFullPath(e.LogPath).StartsWith(root,StringComparison.OrdinalIgnoreCase)||!File.Exists(e.LogPath)||StateStore.HashFile(e.LogPath)!=e.LogSha256||StateStore.HashFile(typeof(ProjectRuntime).Assembly.Location)!=e.RuntimeSha256||!Regex.IsMatch(e.EnvironmentSha256,"^[A-F0-9]{64}$")||e.Command!=w.ValidationCommand)throw new InvalidOperationException("Validation evidence changed.");
  var project=_state.ProjectPath;
  if((await Commands.Git(project,"rev-parse","--verify","refs/agent-os/candidates/"+w.Id)).Checked()!=w.CandidateCommit)throw new InvalidOperationException("Candidate ref changed.");
  var head=(await Commands.Git(project,"rev-parse","--verify",IntegratedRef)).Checked();
  if(head!=_state.IntegratedCommit||(await Commands.Git(project,"merge-base","--is-ancestor",commit,head)).ExitCode!=0)throw new InvalidOperationException("Integrated ancestry changed.");
  var tree=(await Commands.Git(project,"rev-parse",commit+"^{tree}")).Checked();var parent=(await Commands.Git(project,"rev-parse",commit+"^1")).Checked();
  if(tree!=e.Tree||parent!=e.AgainstCommit)throw new InvalidOperationException("Validated tree or precondition changed.");
  return(w,e,tree);
 }
 async Task<EffectScope> Scope(ExternalEffectRequest r,CancellationToken ct)
 {
  var(w,e,tree)=await Verified(r.WorkId,ct);var settings=ExternalSettings;var commit=w.IntegratedCommit!;
  var evidence=H(JsonSerializer.Serialize(new{e.Commit,e.Tree,e.AgainstCommit,e.Command,e.EnvironmentSha256,e.RuntimeSha256,e.LogSha256,e.ExitCode,e.SourceUnchanged,e.TestedAt}));
  var artifact=H(tree);var command=H(e.Command);var environment=e.EnvironmentSha256;string destination,parameters;
  switch(r.Provider)
  {
   case "github":
    var g=settings.GitHub??throw new InvalidOperationException("GitHub provider is not configured.");
    if(r.Operation is not("branch" or "pull_request")||r.Operation=="pull_request"&&string.IsNullOrWhiteSpace(r.Title))throw new ArgumentException("Choose branch or titled pull request.");
    destination=g.ApiOrigin.TrimEnd('/')+"/"+g.Owner+"/"+g.Repository;
    parameters=JsonSerializer.Serialize(new GitHubEffect(g.ApiOrigin.TrimEnd('/'),g.Owner,g.Repository,g.Branch,commit,null,g.BaseBranch,r.Title,r.Body));break;
   case "postgresql":
    var p=settings.PostgreSql??throw new InvalidOperationException("PostgreSQL provider is not configured.");
    if(r.Operation!="migration")throw new ArgumentException("Choose migration.");
    var sql=await Blob(commit,r.SourcePath??throw new ArgumentException("SQL path required."));
    command=H(sql);destination=H(p.Host.ToLowerInvariant()+":"+p.Port+"\n"+p.Database);
    parameters=JsonSerializer.Serialize(new PostgreSqlMigration(destination,p.Schema,r.SourcePath!,sql,command));break;
   case "deployment":
    var d=settings.Deployment??throw new InvalidOperationException("Deployment provider is not configured.");
    if(r.Operation!="deploy")throw new ArgumentException("Choose deploy.");
    artifact=H(await Blob(commit,d.ArtifactPath));command=H(d.Command);environment=H(e.EnvironmentSha256+"\n"+d.EnvironmentName);
    destination=d.Destination;parameters=JsonSerializer.Serialize(new DeploymentEffect(d.Backend,artifact,command,environment,destination));break;
   default:throw new ArgumentException("Unknown provider.");
  }
  return new EffectScope(r.Provider,commit,evidence,artifact,command,environment,destination,r.Operation,parameters);
 }
 IEffectAdapter Adapter(string kind,HttpMessageHandler? transport,Func<DbConnection>? connection,IIsolatedDeploymentExecutor? executor)=>kind switch
 {
  "github" when ExternalSettings.GitHub is { } g=>new GitHubEffects(Journal,new PersistedGitHubCredentials(),transport,[g.ApiOrigin]),
  "postgresql" when ExternalSettings.PostgreSql is { } p=>new PostgreSqlEffects(Journal,connection??PgConnection(p)),
  "deployment" when ExternalSettings.Deployment is { } d=>new DeploymentEffects(Journal,executor??ConfiguredDeploymentExecutor(d)),
  _=>throw new InvalidOperationException(kind+" provider is not configured.")
 };
 IIsolatedDeploymentExecutor? ConfiguredDeploymentExecutor(DeploymentEffectSettings d)
 {
  if(d.Backend!="hyperv")return null;
  HyperVProfile? profile;
  try{profile=new ExecutionProfileRegistry(_store.Root).Current.HyperV;}catch{return null;}
  return profile==null?null:new HyperVExecution(_store.Root,profile,d,commit=>Blob(commit,d.ArtifactPath));
 }
 static Func<DbConnection>? PgConnection(PostgreSqlEffectSettings p)
 {var(factory,_)=PostgreSqlProvider.Discover();if(factory==null)return null;var b=new DbConnectionStringBuilder{["Host"]=p.Host,["Port"]=p.Port,["Database"]=p.Database,["Username"]=p.User,["Integrated Security"]=true,["Pooling"]=false};return PostgreSqlProvider.ConnectionFactory(factory,b.ConnectionString);}
 async Task<T> Guard<T>(Func<Task<T>> f,CancellationToken ct){await _publication.WaitAsync(ct);try{return await f();}finally{_publication.Release();}}
 static string Id(ExternalEffectRequest r)=>H(JsonSerializer.Serialize(r));
 public Task<EffectIntent> PrepareExternalEffectAsync(ExternalEffectRequest r,CancellationToken ct=default)=>Guard(async()=>{var scope=await Scope(r,ct);return await Adapter(r.Provider,null,null,null).PrepareAsync(Id(r),scope,ct);},ct);
 async Task<(EffectIntent,IEffectAdapter)> Checked(string id,HttpMessageHandler? transport,Func<DbConnection>? connection,IIsolatedDeploymentExecutor? executor,CancellationToken ct)
 {
  var intent=Journal.Read(id)??throw new InvalidOperationException("Unknown effect intent.");
  var source=intent.Scope.Kind=="postgresql"?JsonSerializer.Deserialize<PostgreSqlMigration>(intent.Scope.ParametersJson)?.MigrationId:null;
  var gh=intent.Scope.Kind=="github"?JsonSerializer.Deserialize<GitHubEffect>(intent.Scope.ParametersJson):null;
  var work=Snapshot.Work.SingleOrDefault(w=>w.IntegratedCommit==intent.Scope.CandidateCommit&&w.Status==WorkStatus.Completed&&Id(new(w.Id,intent.Scope.Kind,intent.Scope.Operation,source,gh?.PullTitle,gh?.PullBody))==id)??throw new InvalidOperationException("Prepared work identity changed.");
  var current=await Scope(new(work.Id,intent.Scope.Kind,intent.Scope.Operation,source,gh?.PullTitle,gh?.PullBody),ct);
  if(current!=intent.Scope)throw new InvalidOperationException("Effect scope is stale.");
  return(intent,Adapter(intent.Scope.Kind,transport,connection,executor));
 }
 public Task<EffectIntent> ApproveExternalEffectAsync(string id,string digest,string approver,CancellationToken ct=default)=>Guard(async()=>{await Checked(id,null,null,null,ct);return Journal.Approve(id,digest,approver);},ct);
 public Task<EffectIntent> ExecuteExternalEffectAsync(string id,HttpMessageHandler? transport=null,Func<DbConnection>? connection=null,IIsolatedDeploymentExecutor? executor=null,CancellationToken ct=default)=>Guard(async()=>
 {
  var(intent,adapter)=await Checked(id,transport,connection,executor,ct);
  if(intent.State is ExternalEffectState.Unknown or ExternalEffectState.InFlight)throw new InvalidOperationException("Unknown outcome requires reconciliation; no replay.");
  using var ownership=await new MachineCoordinator().EnterAsync("external:"+_state.ProjectPath+":"+intent.Scope.Destination,"external effect",null,ct);
  (_,adapter)=await Checked(id,transport,connection,executor,ct);
  return await adapter.ExecuteAsync(id,ct);
 },ct);
 public Task<EffectIntent> ReconcileExternalEffectAsync(string id,HttpMessageHandler? transport=null,Func<DbConnection>? connection=null,IIsolatedDeploymentExecutor? executor=null,CancellationToken ct=default)=>Guard(async()=>{var(_,adapter)=await Checked(id,transport,connection,executor,ct);return await adapter.ReconcileAsync(id,ct);},ct);
 public Task<EffectIntent> CancelExternalEffectAsync(string id,CancellationToken ct=default)=>Guard(async()=>{var(_,adapter)=await Checked(id,null,null,null,ct);return await adapter.CancelAsync(id,ct);},ct);
}
// Only persistent generic Windows credentials are eligible for an external operation.
internal sealed class PersistedGitHubCredentials : IScopedCredentialProvider
{
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
 struct NativeCredential
 {
  public uint Flags,Type;public string TargetName,Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
  public uint CredentialBlobSize;public IntPtr CredentialBlob;public uint Persist,AttributeCount;public IntPtr Attributes;public string TargetAlias,UserName;
 }
 [System.Runtime.InteropServices.DllImport("advapi32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,EntryPoint="CredReadW")]
 [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
 static extern bool CredRead(string target,uint type,uint flags,out IntPtr credential);
 [System.Runtime.InteropServices.DllImport("advapi32.dll",EntryPoint="CredFree")]static extern void CredFree(IntPtr credential);
 public string? GetSecret(string target)
 {
  if(!OperatingSystem.IsWindows())return null;
  if(!target.StartsWith("AgentOS/GitHub/",StringComparison.Ordinal)||target.Length>256)throw new ArgumentException("Unscoped credential target.");
  if(!CredRead(target,1,0,out var ptr))return null;
  try
  {
   var c=System.Runtime.InteropServices.Marshal.PtrToStructure<NativeCredential>(ptr);
   if(c.Persist<2||c.CredentialBlobSize is 0 or >8192)return null;
   var bytes=new byte[c.CredentialBlobSize];System.Runtime.InteropServices.Marshal.Copy(c.CredentialBlob,bytes,0,bytes.Length);
   try{return Encoding.Unicode.GetString(bytes).TrimEnd('\0');}finally{CryptographicOperations.ZeroMemory(bytes);}
  }
  finally{CredFree(ptr);}
 }
}


