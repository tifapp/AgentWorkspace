using System.Diagnostics;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
public sealed record GitHubEffectSettings(string ApiOrigin,string Owner,string Repository,string Branch,string BaseBranch,string? BaseCommit=null);
public sealed record PostgreSqlEffectSettings(string Host,int Port,string Database,string Schema,string User,string? TrustedRootCertificate=null,bool SchemaRestrictedRole=false);
public sealed record DeploymentEffectSettings(string Backend,string Destination,string ArtifactPath,string Command,string EnvironmentName,DeploymentReceiptContract? Receipt=null);
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
  if(settings.GitHub is { } g && (!Uri.TryCreate(g.ApiOrigin,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.AbsolutePath!="/"||uri.Query.Length!=0||uri.Fragment.Length!=0||!Regex.IsMatch(g.Owner,"^[A-Za-z0-9-]{1,39}$")||!Regex.IsMatch(g.Repository,"^[A-Za-z0-9_.-]{1,100}$")||!Regex.IsMatch(g.Branch,"^[A-Za-z0-9_./-]{1,200}$")||g.Branch.StartsWith('-')||g.Branch.Contains("..")||g.Branch.Contains("@{")||(!Regex.IsMatch(g.BaseBranch,"^[A-Za-z0-9_./-]{1,200}$")||g.BaseBranch.StartsWith('-')||g.BaseBranch.Contains("..")||g.BaseBranch.Contains("@{")||g.BaseCommit!=null&&!Regex.IsMatch(g.BaseCommit,"^[a-fA-F0-9]{40}$"))))throw new ArgumentException("Invalid GitHub settings.");
  if(settings.PostgreSql is { } p && (string.IsNullOrWhiteSpace(p.Host)||p.Host.Length>100||p.Host.Any(c=>!(char.IsLetterOrDigit(c)||c is '.' or '-'))||p.Port is <1 or >65535||!Regex.IsMatch(p.Database,"^[A-Za-z_][A-Za-z0-9_-]{0,62}$")||!Regex.IsMatch(p.Schema,"^[a-z_][a-z0-9_]{0,62}$")||!Regex.IsMatch(p.User,"^[A-Za-z_][A-Za-z0-9_-]{0,62}$")||!p.SchemaRestrictedRole||string.IsNullOrWhiteSpace(p.TrustedRootCertificate)||!Path.IsPathFullyQualified(p.TrustedRootCertificate)||!File.Exists(p.TrustedRootCertificate)))throw new ArgumentException("Invalid PostgreSQL settings.");
  if(settings.Deployment is { } d){if(new[]{d.Backend,d.Destination,d.ArtifactPath,d.Command,d.EnvironmentName}.Any(string.IsNullOrWhiteSpace)||d.Command.Length>8192)throw new ArgumentException("Invalid deployment settings.");if(d.Receipt!=null)DeploymentReceiptProbe.Validate(d.Receipt);}
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
 async Task<byte[]> BinaryBlob(string commit,string path,CancellationToken ct=default)
 {
  path=PathArg(path);var entry=(await Commands.Git(_state.ProjectPath,"ls-tree",commit,"--",path)).Checked();
  if(!entry.StartsWith("100644 blob ")&&!entry.StartsWith("100755 blob "))throw new InvalidOperationException("Regular tracked Git blob required.");
  var oid=entry.Split(' ','\t',StringSplitOptions.RemoveEmptyEntries).ElementAt(2);var size=long.Parse((await Commands.Git(_state.ProjectPath,"cat-file","-s",oid)).Checked());
  if(size is <1 or >1048576)throw new InvalidDataException("Artifact size exceeds bound.");
  var psi=new ProcessStartInfo("git"){WorkingDirectory=_state.ProjectPath,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{"cat-file","blob",oid})psi.ArgumentList.Add(arg);
  psi.Environment["GIT_TERMINAL_PROMPT"]="0";psi.Environment["GIT_CONFIG_NOSYSTEM"]="1";psi.Environment["GIT_CONFIG_GLOBAL"]="NUL";
  using var process=Process.Start(psi)??throw new InvalidOperationException("Git unavailable.");
  var error=process.StandardError.ReadToEndAsync(ct);using var output=new MemoryStream();var buffer=new byte[8192];int n;
  try{while((n=await process.StandardOutput.BaseStream.ReadAsync(buffer,ct))!=0){if(output.Length+n>size){process.Kill(true);throw new InvalidDataException("Git blob exceeds approved size.");}output.Write(buffer,0,n);}await process.WaitForExitAsync(ct);}
  catch(OperationCanceledException){if(!process.HasExited)process.Kill(true);throw;}
  _=await error;var data=output.ToArray();if(process.ExitCode!=0||data.Length!=size)throw new InvalidDataException("Git blob unavailable.");
  var framed=Encoding.ASCII.GetBytes("blob "+data.Length+"\0").Concat(data).ToArray();if(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(framed)).ToLowerInvariant()!=oid)throw new InvalidDataException("Git blob hash mismatch.");return data;
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
    if(r.Operation is not("branch" or "pull_request")||r.Operation=="pull_request"&&(string.IsNullOrWhiteSpace(r.Title)||string.IsNullOrWhiteSpace(g.BaseCommit)))throw new ArgumentException("Choose branch or titled pull request.");
    destination=g.ApiOrigin.TrimEnd('/')+"/"+g.Owner+"/"+g.Repository;
    var snapshot=await GitObjectSnapshot.CaptureAsync(_state.ProjectPath,commit,ct);if(snapshot.Commit.Tree!=tree||snapshot.Commit.Parents[0]!=e.AgainstCommit)throw new InvalidOperationException("Git snapshot differs from evidence.");parameters=JsonSerializer.Serialize(new GitHubEffect(g.ApiOrigin.TrimEnd('/'),g.Owner,g.Repository,g.Branch,commit,null,g.BaseBranch,r.Title,r.Body,snapshot,g.BaseCommit));break;
   case "postgresql":
    var p=settings.PostgreSql??throw new InvalidOperationException("PostgreSQL provider is not configured.");
    if(r.Operation!="migration")throw new ArgumentException("Choose migration.");
    var sql=await Blob(commit,r.SourcePath??throw new ArgumentException("SQL path required."));
    PostgreSqlMigrationGuard.Validate(sql,p.Schema);command=H(sql);environment=H(e.EnvironmentSha256+"\n"+StateStore.HashFile(p.TrustedRootCertificate!));destination=PostgreSqlProvider.EndpointIdentity(p.Host,p.Port,p.Database);
    parameters=JsonSerializer.Serialize(new PostgreSqlMigration(destination,p.Schema,r.SourcePath!,sql,command,p.User,p.SchemaRestrictedRole,new PostgreSqlEndpoint(p.Host.ToLowerInvariant(),p.Port,p.Database,p.User,p.TrustedRootCertificate!,StateStore.HashFile(p.TrustedRootCertificate!))));break;
   case "deployment":
    var d=settings.Deployment??throw new InvalidOperationException("Deployment provider is not configured.");
    if(r.Operation!="deploy")throw new ArgumentException("Choose deploy.");
    artifact=Convert.ToHexString(SHA256.HashData(await BinaryBlob(commit,d.ArtifactPath,ct)));command=H(d.Command);environment=H(e.EnvironmentSha256+"\n"+d.EnvironmentName);
    destination=d.Destination;HyperVProfile? frozenProfile=null;try{frozenProfile=new ExecutionProfileRegistry(_store.Root).Current.HyperV;}catch{}parameters=JsonSerializer.Serialize(new DeploymentEffect(d.Backend,artifact,command,environment,destination,d.Receipt,frozenProfile==null?null:new DeploymentExecutionSnapshot(frozenProfile,d)));break;
   default:throw new ArgumentException("Unknown provider.");
  }
  return new EffectScope(r.Provider,commit,evidence,artifact,command,environment,destination,r.Operation,parameters);
 }
 IEffectAdapter Adapter(string kind,HttpMessageHandler? transport,Func<DbConnection>? connection,IIsolatedDeploymentExecutor? executor)=>kind switch
 {
  "github" when ExternalSettings.GitHub is { } g=>new GitHubEffects(Journal,new PersistedGitHubCredentials(),transport,[g.ApiOrigin]),
  "postgresql" when ExternalSettings.PostgreSql is { } p=>new PostgreSqlEffects(Journal,connection??PgConnection(p)),
  "deployment" when ExternalSettings.Deployment is { } d=>new DeploymentEffects(Journal,executor??ConfiguredDeploymentExecutor(d),new DeploymentReceiptProbe(new WindowsCredentialManagerProvider(),transport)),
  _=>throw new InvalidOperationException(kind+" provider is not configured.")
 };
 IIsolatedDeploymentExecutor? ConfiguredDeploymentExecutor(DeploymentEffectSettings d)
 {
  if(d.Backend!="hyperv")return null;
  HyperVProfile? profile;
  try{profile=new ExecutionProfileRegistry(_store.Root).Current.HyperV;}catch{return null;}
  return profile==null?null:new HyperVExecution(_store.Root,profile,d,commit=>BinaryBlob(commit,d.ArtifactPath));
 }
 static Func<DbConnection> PgConnection(PostgreSqlEffectSettings p)=>PostgreSqlProvider.SecureConnectionFactory(p,new WindowsCredentialManagerProvider());
 async Task<T> Guard<T>(Func<Task<T>> f,CancellationToken ct,bool admit=false){lock(_sync){if(admit)RequireUpdateAdmission();_externalExecuting++;}try{await _publication.WaitAsync(ct);try{return await f();}finally{_publication.Release();}}finally{lock(_sync)_externalExecuting--;}}
 static string Id(ExternalEffectRequest r)=>H(JsonSerializer.Serialize(r));
 static string ExternalAdmissionKey(EffectScope scope)
 {
  if(scope.Kind=="github"){var g=JsonSerializer.Deserialize<GitHubEffect>(scope.ParametersJson)!;return "external:github:"+new Uri(g.ApiOrigin).Authority.ToLowerInvariant()+"/"+g.Owner.ToLowerInvariant()+"/"+g.Repository.ToLowerInvariant()+"/heads/"+g.Branch;}
  if(scope.Kind=="postgresql"){var p=JsonSerializer.Deserialize<PostgreSqlMigration>(scope.ParametersJson)!;return "external:postgresql:"+scope.Destination+":"+p.Schema;}
  return "external:"+scope.Kind+":"+scope.Destination;
 }
 public Task<EffectIntent> PrepareExternalEffectAsync(ExternalEffectRequest r,CancellationToken ct=default)=>Guard(async()=>{var scope=await Scope(r,ct);return await Adapter(r.Provider,null,null,null).PrepareAsync(Id(r),scope,ct);},ct,true);
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
 public Task<EffectIntent> ApproveExternalEffectAsync(string id,string digest,string approver,CancellationToken ct=default)=>Guard(async()=>{await Checked(id,null,null,null,ct);return Journal.Approve(id,digest,approver);},ct,true);
 public Task<EffectIntent> ExecuteExternalEffectAsync(string id,HttpMessageHandler? transport=null,Func<DbConnection>? connection=null,IIsolatedDeploymentExecutor? executor=null,CancellationToken ct=default)=>Guard(async()=>
 {
  var(intent,adapter)=await Checked(id,transport,connection,executor,ct);
  if(intent.State is ExternalEffectState.Unknown or ExternalEffectState.InFlight)throw new InvalidOperationException("Unknown outcome requires reconciliation; no replay.");
  using var ownership=await Coordinator.EnterAsync(ExternalAdmissionKey(intent.Scope),"external effect",null,ct);
  (_,adapter)=await Checked(id,transport,connection,executor,ct);
  return await adapter.ExecuteAsync(id,ct);
 },ct,true);
 IEffectAdapter FrozenAdapter(EffectIntent intent,HttpMessageHandler? transport,Func<DbConnection>? connection,IIsolatedDeploymentExecutor? executor)
 {
  if(intent.Scope.Kind=="github"){var g=JsonSerializer.Deserialize<GitHubEffect>(intent.Scope.ParametersJson)??throw new InvalidDataException("Frozen GitHub destination absent.");if(g.ApiOrigin+"/"+g.Owner+"/"+g.Repository!=intent.Scope.Destination)throw new InvalidDataException("Frozen destination changed.");return new GitHubEffects(Journal,new PersistedGitHubCredentials(),transport,[g.ApiOrigin]);}
  if(intent.Scope.Kind=="postgresql"){
   if(connection!=null)return new PostgreSqlEffects(Journal,connection);
   var p=JsonSerializer.Deserialize<PostgreSqlMigration>(intent.Scope.ParametersJson);var ep=p?.Endpoint;
   if(ep==null||!p!.SchemaRestrictedRole||ep.User!=p.Role||!Regex.IsMatch(ep.Host,"^[A-Za-z0-9.-]{1,100}$")||ep.Port is <1 or >65535||PostgreSqlProvider.EndpointIdentity(ep.Host,ep.Port,ep.Database)!=intent.Scope.Destination||!File.Exists(ep.TrustedRootCertificate)||StateStore.HashFile(ep.TrustedRootCertificate)!=ep.TrustedRootSha256)
    return new PostgreSqlEffects(Journal,null,"Frozen PostgreSQL destination or trust unavailable; outcome unknown.");
   return new PostgreSqlEffects(Journal,PgConnection(new(ep.Host,ep.Port,ep.Database,p.Schema,ep.User,ep.TrustedRootCertificate,true)));
  }
  if(intent.Scope.Kind=="deployment"){var d=JsonSerializer.Deserialize<DeploymentEffect>(intent.Scope.ParametersJson)??throw new InvalidDataException("Frozen deployment absent.");IIsolatedDeploymentExecutor? frozen=executor;if(frozen==null&&d.Execution is { } saved&&d.Backend=="hyperv"&&saved.Settings.Backend==d.Backend&&saved.Settings.Destination==d.Destination&&H(saved.Settings.Command)==d.CommandSha256&&JsonSerializer.Serialize(saved.Settings.Receipt)==JsonSerializer.Serialize(d.Receipt))frozen=new HyperVExecution(_store.Root,saved.Profile,saved.Settings,commit=>BinaryBlob(commit,saved.Settings.ArtifactPath));return new DeploymentEffects(Journal,frozen,new DeploymentReceiptProbe(new WindowsCredentialManagerProvider(),transport));}
  throw new InvalidDataException("Unknown frozen provider.");
 }
 public Task<EffectIntent> ReconcileExternalEffectAsync(string id,HttpMessageHandler? transport=null,Func<DbConnection>? connection=null,IIsolatedDeploymentExecutor? executor=null,CancellationToken ct=default)=>Guard(async()=>
 {
  var intent=Journal.Read(id)??throw new InvalidOperationException("Unknown effect intent.");
  if(intent.State is not(ExternalEffectState.Unknown or ExternalEffectState.InFlight))return intent;
  if(intent.Approval?.ScopeDigest!=intent.Scope.Digest){using var held=Journal.Lock(id);intent=intent with{State=ExternalEffectState.Unknown,Detail="Saved approval digest differs; outcome unknown.",UpdatedAt=DateTimeOffset.UtcNow};Journal.Save(intent);return intent;}
  try{return await FrozenAdapter(intent,transport,connection,executor).ReconcileAsync(id,ct);}
  catch(Exception e)when(e is not OperationCanceledException){using var held=Journal.Lock(id);intent=Journal.Read(id)!;intent=intent with{State=ExternalEffectState.Unknown,Detail="Frozen destination cannot be verified: "+e.GetType().Name,UpdatedAt=DateTimeOffset.UtcNow};Journal.Save(intent);return intent;}
 },ct);
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











