using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace AgentOS.Core;
public sealed record RuntimeProtocol(int Version,int StateSchema,string AssemblySha256,string[] Capabilities);
public sealed record UpdateScope(string PackageSha256,string Version,string Architecture,string InstallRoot,string ProjectPath,string PackagePath,string SignerThumbprint);
public sealed record UpdateDrain(string Token,UpdateScope Scope,DateTimeOffset CreatedUtc);
public sealed record UpdateReadiness(bool Ready,string[] Blockers,long StateGeneration,RuntimeProtocol Protocol,string StatePath);
public static class RuntimeUpdate
{
 public const int Version=1,StateSchema=3;
 public static RuntimeProtocol Health()=>new(Version,StateSchema,StateStore.HashFile(typeof(ProjectRuntime).Assembly.Location),["scoped-drain","cooperative-exit","schema3","signed-msix-handshake"]);
 public static bool IsNewerVersion(string candidate,string installed)=>System.Version.TryParse(candidate,out var n)&&System.Version.TryParse(installed,out var o)&&n>o;
 public static bool CanRollback(long beforeGeneration,int beforeSchema,long currentGeneration,int currentSchema,bool refsUnchanged,bool stopped)=>refsUnchanged&&stopped&&beforeGeneration==currentGeneration&&currentSchema<=beforeSchema;
 public static void Validate(UpdateScope s)
 {
  if(s.PackageSha256.Length!=64||!s.PackageSha256.All(Uri.IsHexDigit))throw new ArgumentException("Invalid package SHA256.");
  if(s.Version.Split('.').Length!=4||!System.Version.TryParse(s.Version,out _))throw new ArgumentException("Invalid package version.");
  if(s.Architecture!="x64"||!Environment.Is64BitOperatingSystem)throw new ArgumentException("Unsupported package architecture.");
  if(!Path.IsPathFullyQualified(s.InstallRoot)||!Path.IsPathFullyQualified(s.ProjectPath)||!Path.IsPathFullyQualified(s.PackagePath))throw new ArgumentException("Absolute update paths required.");
  if(s.SignerThumbprint.Length!=40||!s.SignerThumbprint.All(Uri.IsHexDigit))throw new ArgumentException("Invalid trusted signer thumbprint.");
 }
}
public sealed partial class ProjectRuntime
{
 private UpdateDrain? _updateDrain;
 private int _externalExecuting,_publicationRequests;
 private string UpdateDrainPath=>Path.Combine(_store.Root,"update-drain.json");
 public event Action? CooperativeExitRequested;
 public RuntimeProtocol UpdateHealth=>RuntimeUpdate.Health();
 private void RequireUpdateAdmission(){if(_updateDrain!=null)throw new InvalidOperationException("Project draining for signed update; new work unavailable.");}
 private IDisposable EnterUpdatePublication(){lock(_sync){RequireUpdateAdmission();_publicationRequests++;}return new UpdatePublicationLease(this);}
 private sealed class UpdatePublicationLease(ProjectRuntime owner):IDisposable{public void Dispose(){lock(owner._sync)owner._publicationRequests--;}}
 private void RestoreUpdateDrain()
 {
  if(!File.Exists(UpdateDrainPath))return;
  var saved=JsonSerializer.Deserialize<UpdateDrain>(File.ReadAllText(UpdateDrainPath),JsonFormat.Options)??throw new InvalidDataException("Saved update drain missing.");
  RuntimeUpdate.Validate(saved.Scope);
  if(!string.Equals(Path.GetFullPath(saved.Scope.ProjectPath),_state.ProjectPath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Saved update drain project differs from owner.");
  var metadata=Path.Combine(saved.Scope.InstallRoot,"package-manifest.json");
  if(!File.Exists(metadata))throw new InvalidDataException("Saved drain installation metadata missing.");
  using var doc=JsonDocument.Parse(File.ReadAllText(metadata));
  if(RuntimeUpdate.IsNewerVersion(saved.Scope.Version,doc.RootElement.GetProperty("Version").GetString()??""))_updateDrain=saved;
 }
 internal UpdateDrain BeginUpdateDrainVerified(UpdateScope s)
 {
  RuntimeUpdate.Validate(s);
  lock(_sync)
  {
   ObjectDisposedException.ThrowIf(_disposed,this);
   if(!string.Equals(Path.GetFullPath(s.ProjectPath),_state.ProjectPath,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Drain project differs from owner.");
   if(_updateDrain!=null){if(_updateDrain.Scope!=s)throw new InvalidOperationException("Another package owns this drain.");return _updateDrain;}
   var drain=new UpdateDrain(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),s,DateTimeOffset.UtcNow);
   var next=UpdateDrainPath+".next";
   using(var stream=new FileStream(next,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,drain,JsonFormat.Options);stream.Flush(true);}
   File.Move(next,UpdateDrainPath,true);_updateDrain=drain;
   Event(null,"UpdateDrain","New work closed for signed update. Existing work may finish or be canceled by user.");Save();return drain;
  }
 }
 private void CheckDrain(string token){if(_updateDrain==null||token.Length!=64||!token.All(Uri.IsHexDigit)||!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(_updateDrain.Token),Convert.FromHexString(token)))throw new UnauthorizedAccessException("Invalid update drain token.");}
 public UpdateReadiness UpdateReady(string token)
 {
  lock(_sync)
  {
   CheckDrain(token);var blockers=new List<string>();
   if(_externalExecuting!=0)blockers.Add("external-execution-active");
   if(_state.Maps.Any(m=>m.Status==MapStatus.Active&&m.Tasks.Any(t=>t.Selected&&t.WorkId==null)))blockers.Add("pending-map-work");
   if(_state.Work.Any(w=>w.IsActive)||_jobs.Values.Any(j=>!j.IsCompleted))blockers.Add("active-work-or-owned-processes");
   if(_publicationRequests!=0||_publication.CurrentCount==0||_state.Work.Any(w=>w.PendingCommit!=null))blockers.Add("publication-writer-or-pending-commit");
   if(_state.Work.Any(w=>w.Status==WorkStatus.Unknown)||_state.Decisions.Any(d=>d.Status==DecisionStatus.Unknown))blockers.Add("unknown-outcome");
   if(ListExternalEffects().Any(e=>e.State is ExternalEffectState.InFlight or ExternalEffectState.Unknown))blockers.Add("external-effect-unknown-or-in-flight");
   return new(blockers.Count==0,blockers.ToArray(),_state.Generation,UpdateHealth,_store.StatePath);
  }
 }
 public void RequestUpdateExit(string token)
 {
  if(!UpdateReady(token).Ready)throw new InvalidOperationException("Update not ready; inspect blockers.");
  _=Task.Run(async()=>{await Task.Delay(250);if(!UpdateReady(token).Ready)return;if(CooperativeExitRequested!=null)CooperativeExitRequested.Invoke();else await DisposeAsync();});
 }
}
public sealed partial class ProjectRuntime
{
 public async Task<UpdateDrain> BeginUpdateDrainAsync(UpdateScope s)
 {
  RuntimeUpdate.Validate(s);
  if(!File.Exists(s.PackagePath)||!string.Equals(StateStore.HashFile(s.PackagePath),s.PackageSha256,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Package hash differs from scope.");
  using var zip=ZipFile.OpenRead(s.PackagePath);
  var entry=zip.GetEntry("AppxManifest.xml")??throw new InvalidDataException("MSIX identity missing.");
  if(entry.Length>1048576)throw new InvalidDataException("MSIX identity too large.");
  using var xml=XmlReader.Create(entry.Open(),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
  var id=XDocument.Load(xml).Descendants().Single(x=>x.Name.LocalName=="Identity");
  if((string?)id.Attribute("Name")!="AgentOS.Desktop"||(string?)id.Attribute("Version")!=s.Version||(string?)id.Attribute("ProcessorArchitecture")!=s.Architecture)throw new UnauthorizedAccessException("MSIX identity differs from scope.");
  var publisher=(string?)id.Attribute("Publisher")??throw new InvalidDataException("MSIX publisher missing.");
  var protocolEntry=zip.GetEntry("update-protocol.json")??throw new InvalidDataException("Signed update protocol missing.");
  if(protocolEntry.Length>65536)throw new InvalidDataException("Signed update protocol too large.");
  using(var protocol=JsonDocument.Parse(protocolEntry.Open()))
  {
   var meta=protocol.RootElement;
   if(meta.GetProperty("Protocol").GetInt32()!=RuntimeUpdate.Version||meta.GetProperty("StateSchema").GetInt32()!=RuntimeUpdate.StateSchema)throw new UnauthorizedAccessException("Signed protocol or schema incompatible.");
   foreach(var part in new[]{("AgentOS.Core.dll","AppBinarySha256"),("cli/AgentOS.Core.dll","BinarySha256")})
   {
    using var stream=(zip.GetEntry(part.Item1)??throw new InvalidDataException("Signed runtime assembly missing.")).Open();
    if(!string.Equals(Convert.ToHexString(SHA256.HashData(stream)),meta.GetProperty(part.Item2).GetString(),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Signed binary hash differs from protocol metadata.");
   }
  }
  var path=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s.PackagePath));var pub=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(publisher));
  var command="$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+path+"'));$pub=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+pub+"'));$s=Get-AuthenticodeSignature -LiteralPath $p;$c=$s.SignerCertificate;if($s.Status -ne 'Valid' -or !$c -or $c.Thumbprint -ne '"+s.SignerThumbprint.ToUpperInvariant()+"' -or $c.Subject -cne $pub){exit 1};$eku=$c.Extensions|Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]}|Select-Object -First 1;if($c.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $c.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or !$eku -or !($eku.EnhancedKeyUsages|Where-Object Value -eq '1.3.6.1.5.5.7.3.3')){exit 1};$chain=[Security.Cryptography.X509Certificates.X509Chain]::new();try{if(!$chain.Build($c)){exit 1}}finally{$chain.Dispose()};Write-Output VALID";
  var result=await Commands.RunAsync(Commands.PowerShell,["-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command))],_state.ProjectPath);
  if(result.ExitCode!=0||result.Output.Trim()!="VALID")throw new UnauthorizedAccessException("Signed package association invalid.");
  using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(s.InstallRoot,"package-manifest.json")));
  var metadata=saved.RootElement;
  if(metadata.GetProperty("IdentityName").GetString()!="AgentOS.Desktop"||metadata.GetProperty("Publisher").GetString()!=publisher||!string.Equals(metadata.GetProperty("SignerThumbprint").GetString(),s.SignerThumbprint,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Installed package identity or signer differs from scope.");
  var installed=Path.GetFullPath(metadata.GetProperty("InstallLocation").GetString()??"").TrimEnd(Path.DirectorySeparatorChar);
  var running=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
  if(!string.Equals(running,installed,StringComparison.OrdinalIgnoreCase)&&!string.Equals(Path.GetDirectoryName(running),installed,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Drain target is not owning installed runtime.");
  if(!RuntimeUpdate.IsNewerVersion(s.Version,metadata.GetProperty("Version").GetString()??""))throw new UnauthorizedAccessException("Update version must advance.");
  return BeginUpdateDrainVerified(s);
 }
}
