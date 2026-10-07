using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Core;
public sealed record HyperVProbe(bool Available,string Reason);
public sealed record HyperVReceipt(string OperationId,string ControlNonce,string ScopeDigest,string EvidenceSha256,string ArtifactSha256,string CommandSha256,string EnvironmentSha256,string DestinationSha256,int ExitCode,string Stdout,string Stderr);
internal sealed record HyperVOwner(Guid VmId,string Nonce,string OperationId,string ScopeDigest,string DiskPath,string BaseHash,string Creator);
internal sealed record HyperVManifest(string Mode,HyperVOwner Owner,HyperVProfile Profile,string Command,DeploymentEffect Effect,string[] Ips,string ArtifactPath,string ReceiptPath,string EvidenceSha256);
public sealed class HyperVExecution : IIsolatedDeploymentExecutor
{
 readonly string root,dataRoot;readonly HyperVProfile profile;readonly DeploymentEffectSettings settings;readonly Func<string,Task<byte[]>> artifact;
 public DeploymentCapability Capability {get;}
 public HyperVExecution(string dataRoot,HyperVProfile profile,DeploymentEffectSettings settings,Func<string,Task<string>> artifact)
 {this.dataRoot=Path.GetFullPath(dataRoot);root=Path.Combine(this.dataRoot,"hyperv-operations");this.profile=profile;this.settings=settings;this.artifact=async commit=>Encoding.UTF8.GetBytes(await artifact(commit));Capability=new("hyperv","vm",settings.Destination,Probe(profile).Available);}
 public HyperVExecution(string dataRoot,HyperVProfile profile,DeploymentEffectSettings settings,Func<string,Task<byte[]>> artifact)
 {this.dataRoot=Path.GetFullPath(dataRoot);root=Path.Combine(this.dataRoot,"hyperv-operations");this.profile=profile;this.settings=settings;this.artifact=artifact;Capability=new("hyperv","vm",settings.Destination,Probe(profile).Available);}
 public static HyperVProbe Probe(HyperVProfile? p,bool requireSwitch=true)
 {
  if(p==null)return new(false,"Hyper-V profile is not configured.");
  try{ExecutionProfileRegistry.Validate(new(ExecutionBackend.HyperV,p));}catch(Exception e){return new(false,e.Message);}
  if(!OperatingSystem.IsWindows())return new(false,"Hyper-V requires Windows.");
  if(!File.Exists(p.BaseVhdPath))return new(false,"Base VHD unavailable.");
  try{if(!StateStore.HashFile(p.BaseVhdPath).Equals(p.BaseVhdSha256,StringComparison.OrdinalIgnoreCase))return new(false,"Base VHD hash changed.");}catch{return new(false,"Base VHD cannot be read.");}
  if(!HyperVCredential.Exists(p.GuestCredentialTarget))return new(false,"Guest credential unavailable.");
  if(p.DestinationCredentialTarget is {} t&&!HyperVCredential.Exists(t))return new(false,"Destination credential unavailable.");
  try{using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();if(!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))return new(false,"Hyper-V administrator access unavailable.");}catch{return new(false,"Hyper-V administrator access unavailable.");}
  if(!Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","Modules","Hyper-V")))return new(false,"Hyper-V PowerShell module unavailable.");
  if(requireSwitch&&!SwitchAvailable(p.SwitchName))return new(false,"Configured Hyper-V switch unavailable.");
  return new(true,"Prerequisites present; runtime ACL and ownership checks pending.");
 }
 static bool SwitchAvailable(string name)
 {
  try
  {
   var exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
   var psi=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-EncodedCommand");
   psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("Import-Module Hyper-V -ErrorAction Stop; Get-VMSwitch -Name $env:AGENTOS_SWITCH -ErrorAction Stop | Out-Null")));
   psi.Environment["AGENTOS_SWITCH"]=name;
   using var process=Process.Start(psi);if(process==null)return false;
   if(!process.WaitForExit(5000)){process.Kill(true);return false;}
   return process.ExitCode==0;
  }catch{return false;}
 }
 internal static string H(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
 static string Dir(string root,string id)=>Path.Combine(root,H(id));
 public static string[] EgressManifest(string destination,IEnumerable<string> pinned)
 {
  if(!Uri.TryCreate(destination,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new ArgumentException("HTTPS destination on port 443 required.");
  var addresses=pinned.Select(IPAddress.Parse).ToArray();
  if(addresses.Any(x=>IPAddress.IsLoopback(x)||x.IsIPv6LinkLocal||x.Equals(IPAddress.Any)||x.Equals(IPAddress.IPv6Any)))throw new ArgumentException("Public destination IP required.");
  var ips=addresses.Select(x=>x.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
  if(ips.Length==0)throw new ArgumentException("Pinned destination IP required.");
  return ["deny|0.0.0.0/0|any","deny|::/0|any",..ips.Select(x=>"allow|"+x+"|TCP:443")];
 }
 static string[] Resolve(HyperVProfile p,string destination)
 {
  var uri=new Uri(destination);var pins=p.PinnedDestinationIps??[];EgressManifest(destination,pins);
  var resolved=IPAddress.TryParse(uri.Host,out var literal)?[literal.ToString()]:Dns.GetHostAddresses(uri.DnsSafeHost).Select(x=>x.ToString()).ToArray();
  if(!pins.Order(StringComparer.Ordinal).SequenceEqual(resolved.Order(StringComparer.Ordinal),StringComparer.Ordinal))throw new InvalidOperationException("Pinned destination resolution changed.");
  return pins;
 }
 public static bool ValidReceipt(HyperVReceipt r,string id,string nonce,EffectScope scope)=>r.OperationId==id&&r.ControlNonce==nonce&&r.ScopeDigest==scope.Digest&&r.EvidenceSha256==scope.EvidenceSha256&&r.ArtifactSha256==scope.ArtifactSha256&&r.CommandSha256==scope.CommandSha256&&r.EnvironmentSha256==scope.EnvironmentSha256&&r.DestinationSha256==H(scope.Destination)&&r.Stdout.Length<=8192&&r.Stderr.Length<=8192;
 EffectScope ScopeFor(string id,DeploymentEffect effect)
 {
  var scope=new EffectIntentJournal(Path.Combine(dataRoot,"external-effects")).Read(id)?.Scope??throw new InvalidDataException("Effect scope unavailable.");
  if(scope.Kind!="deployment"||scope.Operation!="deploy"||JsonSerializer.Serialize(effect)!=scope.ParametersJson||scope.ArtifactSha256!=effect.ArtifactSha256||scope.CommandSha256!=effect.CommandSha256||scope.EnvironmentSha256!=effect.EnvironmentSha256||scope.Destination!=effect.Destination)throw new InvalidDataException("Deployment scope changed.");
  scope.Validate();return scope;
 }
 internal static void Save<T>(string path,T value){var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,JsonSerializer.Serialize(value));File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
 public async Task<EffectOutcome> ExecuteAsync(string id,DeploymentEffect effect,CancellationToken ct)
 {
  var probe=Probe(profile);if(!probe.Available)return new(ExternalEffectState.Unavailable,probe.Reason);
  if(effect.Backend!="hyperv"||effect.Destination!=settings.Destination||effect.CommandSha256!=H(settings.Command))return new(ExternalEffectState.Stale,"Deployment settings changed.");
  var scope=ScopeFor(id,effect);var source=await artifact(scope.CandidateCommit);
  if(Convert.ToHexString(SHA256.HashData(source))!=effect.ArtifactSha256)return new(ExternalEffectState.Stale,"Source artifact changed.");
  string[] ips;try{ips=Resolve(profile,effect.Destination);}catch{return new(ExternalEffectState.Unavailable,"Pinned destination resolution unavailable or changed.");}
  var dir=Dir(root,id);if(Directory.Exists(dir))return new(ExternalEffectState.Unknown,"Owned operation record exists; reconcile before retry.");
  Directory.CreateDirectory(dir);
  var owner=new HyperVOwner(Guid.Empty,Guid.NewGuid().ToString("N"),id,scope.Digest,Path.Combine(dir,"child.vhdx"),profile.BaseVhdSha256,Environment.MachineName+"\\"+Environment.UserName);
  Save(Path.Combine(dir,"owner.json"),owner);
  try
  {
   await File.WriteAllBytesAsync(Path.Combine(dir,"artifact.bin"),source,ct);
   var manifest=Path.Combine(dir,"manifest.json");
   Save(manifest,new HyperVManifest("run",owner,profile,settings.Command,effect,ips,Path.Combine(dir,"artifact.bin"),Path.Combine(dir,"receipt.json"),scope.EvidenceSha256));
   await Invoke(manifest,ct);return ReadOutcome(dir,id,scope);
  }
  catch(OperationCanceledException){return new(ExternalEffectState.Unknown,"Owned VM shutdown unconfirmed after cancellation.");}
  catch{return new(ExternalEffectState.Unknown,"Owned VM outcome unconfirmed; reconcile owned record.");}
 }
 public async Task<EffectOutcome> ReconcileAsync(string id,DeploymentEffect effect,CancellationToken ct)
 {
  var dir=Dir(root,id);var ownerPath=Path.Combine(dir,"owner.json");
  if(!File.Exists(ownerPath))return new(ExternalEffectState.Unknown,"No owned VM record; outcome unconfirmed.");
  HyperVOwner? owner;try{owner=JsonSerializer.Deserialize<HyperVOwner>(File.ReadAllText(ownerPath));}catch{return new(ExternalEffectState.Unknown,"Owned VM record invalid.");}
  if(owner==null||owner.OperationId!=id||owner.ScopeDigest!=ScopeFor(id,effect).Digest)return new(ExternalEffectState.Unknown,"Owned VM record mismatch.");
  if(!File.Exists(Path.Combine(dir,"shutdown-confirmed"))&&Probe(profile).Available)
  {
   var query=Path.Combine(dir,"query.json");Save(query,new HyperVManifest("query",owner,profile,"",effect,[],"",Path.Combine(dir,"receipt.json"),ScopeFor(id,effect).EvidenceSha256));
   try{await Invoke(query,ct);}catch{return new(ExternalEffectState.Unknown,"Owned VM query or shutdown unconfirmed.");}
  }
  return ReadOutcome(dir,id,ScopeFor(id,effect));
 }
 static EffectOutcome ReadOutcome(string dir,string id,EffectScope scope)
 {
  var receipt=Path.Combine(dir,"receipt.json");var confirmed=Path.Combine(dir,"shutdown-confirmed");var owned=Path.Combine(dir,"owner.json");
  try
  {
   var owner=JsonSerializer.Deserialize<HyperVOwner>(File.ReadAllText(owned));
   if(owner==null||owner.OperationId!=id||owner.ScopeDigest!=scope.Digest||!File.Exists(confirmed)||File.ReadAllText(confirmed).Trim()!=owner.Nonce)return new(ExternalEffectState.Unknown,"Owned VM shutdown unconfirmed.");
   if(!File.Exists(receipt))return new(ExternalEffectState.Unknown,"Guest receipt absent; remote outcome unconfirmed.");
   if(new FileInfo(receipt).Length>20000)return new(ExternalEffectState.Unknown,"Guest receipt exceeds bound.");
   var r=JsonSerializer.Deserialize<HyperVReceipt>(File.ReadAllText(receipt));
   if(r==null||!ValidReceipt(r,id,owner.Nonce,scope))return new(ExternalEffectState.Unknown,"Guest receipt scope mismatch.");
   return r.ExitCode==0?new(ExternalEffectState.Completed,"Guest receipt confirmed.",id):new(ExternalEffectState.Unknown,"Guest command failed; remote outcome requires review.",id);
  }catch{return new(ExternalEffectState.Unknown,"Guest receipt invalid or shutdown unconfirmed.");}
 }
 internal static async Task Invoke(string manifest,CancellationToken ct)
 {
  var exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
  var m=JsonSerializer.Deserialize<HyperVManifest>(File.ReadAllText(manifest))!;
  var psi=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-EncodedCommand");psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(HostHelper)));
  psi.Environment["AGENTOS_HYPERV_MANIFEST"]=manifest;
  if(!HyperVCredential.Read(m.Profile.GuestCredentialTarget,out var user,out var secret))throw new InvalidOperationException("Guest credential unavailable.");
  psi.Environment["AGENTOS_GUEST_USER"]=user;psi.Environment["AGENTOS_GUEST_SECRET"]=secret;
  if(m.Profile.DestinationCredentialTarget is {} target){if(!HyperVCredential.Read(target,out var du,out var ds))throw new InvalidOperationException("Destination credential unavailable.");psi.Environment["AGENTOS_DEST_USER"]=du;psi.Environment["AGENTOS_DEST_SECRET"]=ds;}
  using var process=Process.Start(psi)??throw new IOException("Hyper-V helper did not start.");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(20));
  try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{process.Kill(true);}catch{}return;}
 }
 internal const string HostHelper=@"$ErrorActionPreference='Stop'
Import-Module Hyper-V -ErrorAction Stop
$m=Get-Content -LiteralPath $env:AGENTOS_HYPERV_MANIFEST -Raw | ConvertFrom-Json
$o=$m.Owner
$name='AgentOS-'+$o.Nonce
$dir=Split-Path $m.ReceiptPath
if($o.Creator -ne ($env:COMPUTERNAME+'\'+$env:USERNAME)){throw 'Creator identity changed'}
function OwnedVm {
 $vm=Get-VM -Name $name -ErrorAction SilentlyContinue
 if($null -eq $vm){return $null}
 if($vm.Notes -ne $o.Nonce){throw 'VM ownership mismatch'}
 $drives=@(Get-VMHardDiskDrive -VM $vm)
 if($drives.Count -ne 1 -or $drives[0].Path -ne $o.DiskPath){throw 'VM disk ownership mismatch'}
 if([string]$o.VmId -eq '00000000-0000-0000-0000-000000000000'){
  $o.VmId=$vm.Id
  $o | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $dir 'owner.next')
  Move-Item -LiteralPath (Join-Path $dir 'owner.next') -Destination (Join-Path $dir 'owner.json') -Force
 } elseif([string]$vm.Id -ne [string]$o.VmId){throw 'VM ownership mismatch'}
 $drives=@(Get-VMHardDiskDrive -VM $vm)
 if($drives.Count -ne 1 -or $drives[0].Path -ne $o.DiskPath){throw 'VM disk ownership mismatch'}
 return $vm
}
function FinishVm($vm) {
 if($vm.State -eq 'Running'){Stop-VM -VM $vm -Shutdown -Confirm:$false}
 $deadline=(Get-Date).AddSeconds(45)
 while((Get-VM -Id $vm.Id).State -ne 'Off' -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 500}
 $vm=OwnedVm
 if($null -eq $vm -or $vm.State -ne 'Off'){throw 'Owned VM shutdown unconfirmed'}
 Remove-VM -VM $vm -Force
 if(Get-VM -Id $vm.Id -ErrorAction SilentlyContinue){throw 'Owned VM removal unconfirmed'}
 if(Test-Path -LiteralPath $o.DiskPath){Remove-Item -LiteralPath $o.DiskPath -Force}
 Set-Content -LiteralPath (Join-Path $dir 'shutdown-confirmed') -Value $o.Nonce
}
if($m.Mode -eq 'query') {
 $vm=OwnedVm
 if($null -eq $vm){exit 2}
 if($vm.State -ne 'Running'){exit 3}
 $guest=New-Object System.Management.Automation.PSCredential($env:AGENTOS_GUEST_USER,(ConvertTo-SecureString $env:AGENTOS_GUEST_SECRET -AsPlainText -Force))
 $r=Invoke-Command -VMId $vm.Id -Credential $guest -ScriptBlock {if(Test-Path -LiteralPath 'C:\AgentOS\receipt.json'){Get-Content -LiteralPath 'C:\AgentOS\receipt.json' -Raw}}
 if(!$r){exit 4}
 [IO.File]::WriteAllText($m.ReceiptPath,[string]$r)
 FinishVm $vm
 exit 0
}
if($m.Mode -ne 'run'){throw 'Unknown helper mode'}
if(Get-VM -Name $name -ErrorAction SilentlyContinue){throw 'VM name already exists'}
if((Get-FileHash -LiteralPath $m.Profile.BaseVhdPath -Algorithm SHA256).Hash -ne $o.BaseHash){throw 'Base VHD hash changed'}
New-VHD -Path $o.DiskPath -ParentPath $m.Profile.BaseVhdPath -Differencing | Out-Null
$vm=New-VM -Name $name -Generation 2 -MemoryStartupBytes 2GB -VHDPath $o.DiskPath
Set-VM -VM $vm -Notes $o.Nonce
$o.VmId=$vm.Id
$o | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $dir 'owner.next')
Move-Item -LiteralPath (Join-Path $dir 'owner.next') -Destination (Join-Path $dir 'owner.json') -Force
foreach($nic in @(Get-VMNetworkAdapter -VM $vm)){Disconnect-VMNetworkAdapter -VMNetworkAdapter $nic}
Add-VMNetworkAdapter -VMName $vm.Name -Name 'AgentOS-egress' -SwitchName $m.Profile.SwitchName
$adapter=Get-VMNetworkAdapter -VMName $vm.Name -Name 'AgentOS-egress'
if(@(Get-VMNetworkAdapter -VM $vm | Where-Object {$_.SwitchName}).Count -ne 1){throw 'Unexpected connected adapter'}
Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $adapter -Action Deny -Direction Outbound -RemoteIPAddress '0.0.0.0/0' -Weight 1
Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $adapter -Action Deny -Direction Outbound -RemoteIPAddress '::/0' -Weight 1
foreach($ip in $m.Ips){Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $adapter -Action Allow -Direction Outbound -RemoteIPAddress $ip -Protocol TCP -RemotePort 443 -Weight 100}
$acl=@(Get-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $adapter)
if($acl.Count -ne (2+$m.Ips.Count)){throw 'Unexpected egress ACL'}
$denies=@($acl | Where-Object {$_.Action -eq 'Deny' -and $_.Direction -eq 'Outbound' -and $_.Weight -eq 1})
$allows=@($acl | Where-Object {$_.Action -eq 'Allow' -and $_.Direction -eq 'Outbound' -and $_.Weight -eq 100 -and ($_.Protocol -eq 6 -or $_.Protocol -eq 'TCP') -and $_.RemotePort -eq '443'})
if($denies.Count -ne 2 -or $allows.Count -ne $m.Ips.Count){throw 'Effective ACL mismatch'}
if('0.0.0.0/0' -notin @($denies.RemoteIPAddress) -or '::/0' -notin @($denies.RemoteIPAddress)){throw 'Default deny missing'}
foreach($ip in $m.Ips){if($ip -notin @($allows.RemoteIPAddress)){throw 'Pinned allow missing'}}
Enable-VMIntegrationService -VMName $vm.Name -Name 'Guest Service Interface'
Start-VM -VM $vm | Out-Null
$guest=New-Object System.Management.Automation.PSCredential($env:AGENTOS_GUEST_USER,(ConvertTo-SecureString $env:AGENTOS_GUEST_SECRET -AsPlainText -Force))
$ready=(Get-Date).AddMinutes(2);$connected=$false
while((Get-Date) -lt $ready){try{$connected=(Invoke-Command -VMId $vm.Id -Credential $guest -ScriptBlock {'ready'} -ErrorAction Stop) -eq 'ready';if($connected){break}}catch{} Start-Sleep -Seconds 2}
if(!$connected){throw 'PowerShell Direct guest unavailable'}
Copy-VMFile -VMName $vm.Name -SourcePath $m.ArtifactPath -DestinationPath 'C:\AgentOS\artifact.bin' -FileSource Host -CreateFullPath
$e=$m.Effect
$hash=[BitConverter]::ToString(([Security.Cryptography.SHA256]::Create()).ComputeHash([Text.Encoding]::UTF8.GetBytes($e.Destination))).Replace('-','')
$spec=@{OperationId=$o.OperationId;ControlNonce=$o.Nonce;ScopeDigest=$o.ScopeDigest;ArtifactSha256=$e.ArtifactSha256;CommandSha256=$e.CommandSha256;EnvironmentSha256=$e.EnvironmentSha256;EvidenceSha256=$m.EvidenceSha256;DestinationSha256=$hash;Command=$m.Command;DestinationHost=([uri]$e.Destination).DnsSafeHost;PinnedIp=$m.Ips[0]} | ConvertTo-Json -Compress
$r=Invoke-Command -VMId $vm.Id -Credential $guest -ScriptBlock {
 param($json,$destUser,$destSecret)
 $s=$json | ConvertFrom-Json
 if($s.DestinationHost -and $s.PinnedIp){Add-Content -LiteralPath 'C:\Windows\System32\drivers\etc\hosts' -Value ($s.PinnedIp+' '+$s.DestinationHost)}
 $env:AGENTOS_DEST_USER=$destUser;$env:AGENTOS_DEST_SECRET=$destSecret
 $out='';$err='';$code=1
 try{
  $job=Start-Job -ScriptBlock {param($command) try{$text=(& ([scriptblock]::Create($command)) *>&1 | Out-String);return @{Code=$(if($LASTEXITCODE){$LASTEXITCODE}else{0});Output=$text;Error=''}}catch{return @{Code=1;Output='';Error=$_.Exception.GetType().Name}}} -ArgumentList $s.Command
  if(!(Wait-Job $job -Timeout 600)){Stop-Job $job;$code=124;$err='Guest command timed out'}else{$jr=Receive-Job $job;$code=[int]$jr.Code;$out=[string]$jr.Output;$err=[string]$jr.Error}
  Remove-Job $job -Force
 }catch{$err=$_.Exception.GetType().Name;$code=1}
 if($destSecret){$out=$out.Replace([string]$destSecret,'[redacted]');$err=$err.Replace([string]$destSecret,'[redacted]')}
 $receipt=@{OperationId=$s.OperationId;ControlNonce=$s.ControlNonce;ScopeDigest=$s.ScopeDigest;EvidenceSha256=$s.EvidenceSha256;ArtifactSha256=$s.ArtifactSha256;CommandSha256=$s.CommandSha256;EnvironmentSha256=$s.EnvironmentSha256;DestinationSha256=$s.DestinationSha256;ExitCode=$code;Stdout=$out.Substring(0,[Math]::Min(8192,$out.Length));Stderr=$err.Substring(0,[Math]::Min(8192,$err.Length))}
 $receipt | ConvertTo-Json -Compress | Set-Content -LiteralPath 'C:\AgentOS\receipt.json'
 return $receipt
} -ArgumentList $spec,$env:AGENTOS_DEST_USER,$env:AGENTOS_DEST_SECRET
$r | ConvertTo-Json -Compress | Set-Content -LiteralPath $m.ReceiptPath
FinishVm $vm
";
}
internal static class HyperVCredential
{
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
 struct Native {public uint Flags,Type;public string TargetName,Comment;public System.Runtime.InteropServices.ComTypes.FILETIME Written;public uint BlobSize;public IntPtr Blob;public uint Persist,AttributeCount;public IntPtr Attributes;public string Alias,UserName;}
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="CredReadW")][return:MarshalAs(UnmanagedType.Bool)]static extern bool CredRead(string target,uint type,uint flags,out IntPtr ptr);
 [DllImport("advapi32.dll",EntryPoint="CredFree")]static extern void CredFree(IntPtr ptr);
 public static bool Exists(string target)=>Read(target,out _,out _);
 public static bool Read(string target,out string user,out string secret)
 {
  user="";secret="";
  if(!OperatingSystem.IsWindows()||!(target.StartsWith("AgentOS/HyperV/Guest/",StringComparison.Ordinal)||target.StartsWith("AgentOS/HyperV/Destination/",StringComparison.Ordinal))||!CredRead(target,1,0,out var ptr))return false;
  try{var c=Marshal.PtrToStructure<Native>(ptr);if(c.Persist<2||c.BlobSize is 0 or >8192||string.IsNullOrWhiteSpace(c.UserName))return false;
   var bytes=new byte[checked((int)c.BlobSize)];Marshal.Copy(c.Blob,bytes,0,bytes.Length);try{user=c.UserName;secret=Encoding.Unicode.GetString(bytes).TrimEnd('\0');return secret.Length>0;}finally{CryptographicOperations.ZeroMemory(bytes);}
  }finally{CredFree(ptr);}
 }
}
public sealed record SdkGuestRequest(string Script,string PrivateWorkspace,IReadOnlyList<string> Artifacts,int TimeoutSeconds=300);
public sealed record SdkGuestResult(WindowsVmState State,int? ExitCode,string Detail,string Stdout="",string Stderr="",string ReceiptSha256="",string EnvironmentFingerprint="",bool ShutdownConfirmed=false);
internal sealed record SdkManifest(string VmName,string Disk,string Base,string BaseHash,string Nonce,string Creator,string Script,int TimeoutSeconds,SdkFile[] Files,string[] Artifacts,string Result,string Confirmed,string EnvironmentFingerprint);
internal sealed record SdkFile(string Source,string Target);
public sealed class HyperVSdkRunner(HyperVProfile profile)
{
 internal static string SafeRelative(string path)
 {
  if(string.IsNullOrWhiteSpace(path)||Path.IsPathRooted(path)||path.Contains('\\')||path.Split('/').Any(x=>x is "" or "." or ".."||x.Equals(".git",StringComparison.OrdinalIgnoreCase)||x.StartsWith(".agentos-sdk-",StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(x,"^[A-Za-z0-9._-]+$")||System.Text.RegularExpressions.Regex.IsMatch(x.Split('.')[0],"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||x.EndsWith('.'))||path.Length>240)throw new ArgumentException("Checked relative artifact path required.");
  return path;
 }
 public async Task<SdkGuestResult> RunAsync(SdkGuestRequest request,CancellationToken ct=default)
 {
  var probe=HyperVExecution.Probe(profile with{DestinationCredentialTarget=null},false);if(!probe.Available)return new(WindowsVmState.Unavailable,null,probe.Reason);
  if(request.Script.Length is <1 or >8192||request.TimeoutSeconds is <1 or >3600||request.Artifacts.Count>128)throw new ArgumentException("SDK manifest exceeds bounds.");
  var root=ResourceAdmission.Canonical(request.PrivateWorkspace,true);WindowsVmExecution.ValidateTree(root);
  var paths=request.Artifacts.Select(SafeRelative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  var files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(x=>(Absolute:x,Relative:Path.GetRelativePath(root,x).Replace('\\','/'))).Where(x=>!x.Relative.Split('/').Contains(".git",StringComparer.OrdinalIgnoreCase)).ToArray();
  if(files.Length>4096||files.Sum(x=>new FileInfo(x.Absolute).Length)>16*1024*1024)throw new ArgumentException("SDK workspace exceeds copy bounds.");
  var work=Path.Combine(root,".agentos-sdk-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
  var nonce=Guid.NewGuid().ToString("N");
  var copied=new List<SdkFile>();var evidence=new List<string>();
  foreach(var f in files)
  {
   var relative=SafeRelative(f.Relative);var staged=Path.Combine(work,"inputs",relative.Replace('/',Path.DirectorySeparatorChar));
   Directory.CreateDirectory(Path.GetDirectoryName(staged)!);File.Copy(f.Absolute,staged);
   copied.Add(new SdkFile(staged,"C:\\AgentOS\\workspace\\"+relative.Replace('/','\\')));
   evidence.Add(relative+":"+StateStore.HashFile(staged));
  }
  var manifest=new SdkManifest("AgentOS-SDK-"+nonce,Path.Combine(work,"child.vhdx"),profile.BaseVhdPath,profile.BaseVhdSha256,nonce,Environment.MachineName+"\\"+Environment.UserName,request.Script,request.TimeoutSeconds,copied.ToArray(),paths,Path.Combine(work,"result.json"),Path.Combine(work,"shutdown-confirmed"),HyperVExecution.H(profile.BaseVhdSha256+"\n"+request.Script+"\n"+string.Join("\n",evidence)));
  var data=Path.Combine(work,"manifest.json");HyperVExecution.Save(data,manifest);HyperVExecution.Save(Path.Combine(work,"owner.json"),new{VmId=Guid.Empty,Nonce=nonce,Name=manifest.VmName,Disk=manifest.Disk,Creator=manifest.Creator});
  var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-EncodedCommand");psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Helper)));
  if(!HyperVCredential.Read(profile.GuestCredentialTarget,out var user,out var secret))return new(WindowsVmState.Unavailable,null,"Guest credential unavailable.");
  psi.Environment["AGENTOS_SDK_MANIFEST"]=data;psi.Environment["AGENTOS_GUEST_USER"]=user;psi.Environment["AGENTOS_GUEST_SECRET"]=secret;
  using var process=Process.Start(psi)??throw new IOException("SDK VM helper did not start.");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds+180));
  try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{process.Kill(true);}catch{}return new(WindowsVmState.Unknown,null,"SDK VM shutdown unconfirmed; ownership retained at "+work);}
  if(process.ExitCode!=0||!File.Exists(manifest.Confirmed)||File.ReadAllText(manifest.Confirmed).Trim()!=nonce||!File.Exists(manifest.Result))return new(WindowsVmState.Unknown,null,"SDK VM result or shutdown unconfirmed; ownership retained at "+work);
  if(new FileInfo(manifest.Result).Length>2_000_000)return new(WindowsVmState.Unknown,null,"SDK result exceeds bound.");
  var resultBytes=await File.ReadAllBytesAsync(manifest.Result,ct);using var document=JsonDocument.Parse(resultBytes);var value=document.RootElement;
  if(value.GetProperty("Nonce").GetString()!=nonce||value.GetProperty("EnvironmentFingerprint").GetString()!=manifest.EnvironmentFingerprint)return new(WindowsVmState.Unknown,null,"SDK receipt identity mismatch.");
  var output=value.GetProperty("Stdout").GetString()??"";var error=value.GetProperty("Stderr").GetString()??"";
  if(output.Length>8192||error.Length>8192)return new(WindowsVmState.Unknown,null,"SDK output exceeds bound.");
  var artifacts=value.GetProperty("Artifacts");long total=0;
  foreach(var item in artifacts.EnumerateObject())
  {
   var relative=SafeRelative(item.Name);if(!paths.Contains(relative,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Unrequested artifact.");
   var bytes=Convert.FromBase64String(item.Value.GetString()??"");total+=bytes.Length;if(total>1_000_000)throw new InvalidDataException("Artifact result exceeds bound.");
   var destination=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
   if(!destination.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Artifact path escaped workspace.");
   var cursor=Path.GetDirectoryName(destination)!;while(cursor.StartsWith(root,StringComparison.OrdinalIgnoreCase)){if(Directory.Exists(cursor)&&File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Artifact parent is a reparse point.");if(cursor==root)break;cursor=Path.GetDirectoryName(cursor)!;}
   if(File.Exists(destination)&&File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Artifact target is a reparse point.");
   Directory.CreateDirectory(Path.GetDirectoryName(destination)!);var temp=destination+".agentos-"+Guid.NewGuid().ToString("N")+".tmp";try{await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,destination,true);}finally{if(File.Exists(temp))File.Delete(temp);}
  }
  var code=value.GetProperty("ExitCode").GetInt32();var hash=Convert.ToHexString(SHA256.HashData(resultBytes));
  WindowsVmExecution.ValidateTree(work);Directory.Delete(work,true);
  return new(code==0?WindowsVmState.Completed:WindowsVmState.Failed,code,"SDK guest receipt confirmed.",output,error,hash,manifest.EnvironmentFingerprint,true);
 }
 public async Task<SdkGuestResult> RecoverOwnedAsync(string ownedWorkDirectory,CancellationToken ct=default)
 {
  var dir=Path.GetFullPath(ownedWorkDirectory);
  var manifestPath=Path.Combine(dir,"manifest.json");var ownerPath=Path.Combine(dir,"owner.json");
  if(!File.Exists(manifestPath)||!File.Exists(ownerPath))return new(WindowsVmState.Unknown,null,"SDK ownership record unavailable.");
  SdkManifest? m;try{m=JsonSerializer.Deserialize<SdkManifest>(File.ReadAllText(manifestPath));}catch{return new(WindowsVmState.Unknown,null,"SDK manifest invalid.");}
  if(m==null||Path.GetDirectoryName(m.Result)!=dir||m.Disk!=Path.Combine(dir,"child.vhdx")||m.Confirmed!=Path.Combine(dir,"shutdown-confirmed")||m.Creator!=Environment.MachineName+"\\"+Environment.UserName)return new(WindowsVmState.Unknown,null,"SDK ownership identity changed.");
  if(!HyperVExecution.Probe(profile with{DestinationCredentialTarget=null},false).Available)return new(WindowsVmState.Unavailable,null,"Hyper-V prerequisites unavailable.");
  var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-EncodedCommand");psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(RecoveryHelper)));
  if(!HyperVCredential.Read(profile.GuestCredentialTarget,out var user,out var secret))return new(WindowsVmState.Unavailable,null,"Guest credential unavailable.");
  psi.Environment["AGENTOS_SDK_MANIFEST"]=manifestPath;psi.Environment["AGENTOS_GUEST_USER"]=user;psi.Environment["AGENTOS_GUEST_SECRET"]=secret;
  using var process=Process.Start(psi)??throw new IOException("SDK recovery helper did not start.");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(3));
  try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{process.Kill(true);}catch{}return new(WindowsVmState.Unknown,null,"SDK VM shutdown unconfirmed; ownership retained at "+dir);}
  if(process.ExitCode!=0||!File.Exists(m.Confirmed)||File.ReadAllText(m.Confirmed).Trim()!=m.Nonce||!File.Exists(m.Result))return new(WindowsVmState.Unknown,null,"SDK VM shutdown or receipt unconfirmed; ownership retained at "+dir);
  if(new FileInfo(m.Result).Length>2_000_000)return new(WindowsVmState.Unknown,null,"SDK receipt exceeds bound.");
  var bytes=await File.ReadAllBytesAsync(m.Result,ct);
  try{using var doc=JsonDocument.Parse(bytes);var r=doc.RootElement;
   if(r.GetProperty("Nonce").GetString()!=m.Nonce||r.GetProperty("EnvironmentFingerprint").GetString()!=m.EnvironmentFingerprint)return new(WindowsVmState.Unknown,null,"SDK receipt identity mismatch.");
   var output=r.GetProperty("Stdout").GetString()??"";var error=r.GetProperty("Stderr").GetString()??"";
   if(output.Length>8192||error.Length>8192)return new(WindowsVmState.Unknown,null,"SDK output exceeds bound.");
   return new(WindowsVmState.Unknown,r.GetProperty("ExitCode").GetInt32(),"SDK VM shutdown confirmed; recover artifacts from retained receipt.",output,error,Convert.ToHexString(SHA256.HashData(bytes)),m.EnvironmentFingerprint,true);
  }catch{return new(WindowsVmState.Unknown,null,"SDK receipt invalid.");}
 }
 internal const string RecoveryHelper=@"$ErrorActionPreference='Stop'
Import-Module Hyper-V -ErrorAction Stop
$m=Get-Content -LiteralPath $env:AGENTOS_SDK_MANIFEST -Raw | ConvertFrom-Json
$o=Get-Content -LiteralPath (Join-Path (Split-Path $m.Result) 'owner.json') -Raw | ConvertFrom-Json
if($m.Creator -ne ($env:COMPUTERNAME+'\'+$env:USERNAME) -or $o.Creator -ne $m.Creator -or $o.Nonce -ne $m.Nonce -or $o.Name -ne $m.VmName -or $o.Disk -ne $m.Disk){throw 'SDK owner identity changed'}
if([string]$o.VmId -eq '00000000-0000-0000-0000-000000000000'){
 $vm=Get-VM -Name $m.VmName -ErrorAction SilentlyContinue
 if($null -ne $vm){
  $firstDrive=@(Get-VMHardDiskDrive -VM $vm)
  if($vm.Notes -ne $m.Nonce -or $firstDrive.Count -ne 1 -or $firstDrive[0].Path -ne $m.Disk){throw 'SDK VM ownership mismatch'}
  $o.VmId=$vm.Id
  $o | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path (Split-Path $m.Result) 'owner.next')
  Move-Item -LiteralPath (Join-Path (Split-Path $m.Result) 'owner.next') -Destination (Join-Path (Split-Path $m.Result) 'owner.json') -Force
 }
} else {$vm=Get-VM -Id $o.VmId -ErrorAction SilentlyContinue}
if($null -eq $vm){if((Test-Path -LiteralPath $m.Confirmed) -and (Get-Content -LiteralPath $m.Confirmed -Raw).Trim() -eq $m.Nonce){exit 0};throw 'SDK VM missing without shutdown marker'}
$drive=@(Get-VMHardDiskDrive -VM $vm)
if($vm.Name -ne $m.VmName -or $vm.Notes -ne $m.Nonce -or $drive.Count -ne 1 -or $drive[0].Path -ne $m.Disk){throw 'SDK VM ownership mismatch'}
if(!(Test-Path -LiteralPath $m.Result) -and $vm.State -eq 'Running'){
 $guest=New-Object System.Management.Automation.PSCredential($env:AGENTOS_GUEST_USER,(ConvertTo-SecureString $env:AGENTOS_GUEST_SECRET -AsPlainText -Force))
 $r=Invoke-Command -VMId $vm.Id -Credential $guest -ScriptBlock {if(Test-Path -LiteralPath 'C:\AgentOS\sdk-receipt.json'){Get-Content -LiteralPath 'C:\AgentOS\sdk-receipt.json' -Raw}}
 if($r){[IO.File]::WriteAllText($m.Result,[string]$r)}
}
if(!(Test-Path -LiteralPath $m.Result)){throw 'SDK guest receipt absent'}
if($vm.State -eq 'Running'){Stop-VM -VM $vm -Shutdown -Confirm:$false}
$deadline=(Get-Date).AddSeconds(45)
while((Get-VM -Id $vm.Id).State -ne 'Off' -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 500}
$vm=Get-VM -Id $o.VmId
$drive=@(Get-VMHardDiskDrive -VM $vm)
if($vm.State -ne 'Off' -or $vm.Name -ne $m.VmName -or $vm.Notes -ne $m.Nonce -or $drive.Count -ne 1 -or $drive[0].Path -ne $m.Disk){throw 'SDK shutdown unconfirmed'}
Remove-VM -VM $vm -Force
if(Get-VM -Id $o.VmId -ErrorAction SilentlyContinue){throw 'SDK VM removal unconfirmed'}
Remove-Item -LiteralPath $m.Disk -Force
Set-Content -LiteralPath $m.Confirmed -Value $m.Nonce
";
 internal const string Helper=@"$ErrorActionPreference='Stop'
Import-Module Hyper-V -ErrorAction Stop
$m=Get-Content -LiteralPath $env:AGENTOS_SDK_MANIFEST -Raw | ConvertFrom-Json
if($m.Creator -ne ($env:COMPUTERNAME+'\'+$env:USERNAME)){throw 'Creator identity changed'}
if(Get-VM -Name $m.VmName -ErrorAction SilentlyContinue){throw 'SDK VM name already exists'}
if((Get-FileHash -LiteralPath $m.Base -Algorithm SHA256).Hash -ne $m.BaseHash){throw 'Base VHD hash changed'}
New-VHD -Path $m.Disk -ParentPath $m.Base -Differencing | Out-Null
$vm=New-VM -Name $m.VmName -Generation 2 -MemoryStartupBytes 2GB -VHDPath $m.Disk
Set-VM -VM $vm -Notes $m.Nonce
@{VmId=$vm.Id;Nonce=$m.Nonce;Name=$m.VmName;Disk=$m.Disk;Creator=$m.Creator} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path (Split-Path $m.Result) 'owner.json')
foreach($nic in @(Get-VMNetworkAdapter -VM $vm)){Disconnect-VMNetworkAdapter -VMNetworkAdapter $nic}
if(@(Get-VMNetworkAdapter -VM $vm | Where-Object {$_.SwitchName}).Count -ne 0){throw 'SDK VM network adapter remained connected'}
Enable-VMIntegrationService -VMName $vm.Name -Name 'Guest Service Interface'
Start-VM -VM $vm | Out-Null
$guest=New-Object System.Management.Automation.PSCredential($env:AGENTOS_GUEST_USER,(ConvertTo-SecureString $env:AGENTOS_GUEST_SECRET -AsPlainText -Force))
$ready=(Get-Date).AddMinutes(2);$connected=$false
while((Get-Date) -lt $ready){try{$connected=(Invoke-Command -VMId $vm.Id -Credential $guest -ScriptBlock {'ready'} -ErrorAction Stop) -eq 'ready';if($connected){break}}catch{} Start-Sleep -Seconds 2}
if(!$connected){throw 'PowerShell Direct guest unavailable'}
foreach($f in $m.Files){Copy-VMFile -VMName $vm.Name -SourcePath $f.Source -DestinationPath $f.Target -FileSource Host -CreateFullPath}
$spec=@{Nonce=$m.Nonce;Script=$m.Script;Artifacts=@($m.Artifacts);TimeoutSeconds=$m.TimeoutSeconds;EnvironmentFingerprint=$m.EnvironmentFingerprint} | ConvertTo-Json -Compress -Depth 5
$r=Invoke-Command -VMId $vm.Id -Credential $guest -ScriptBlock {
 param($json)
 $s=$json | ConvertFrom-Json
 $root='C:\AgentOS\workspace';New-Item -ItemType Directory -Force -Path $root | Out-Null
 $output='';$errorText='';$code=1
 try{
  $job=Start-Job -ScriptBlock {param($script,$root) $ErrorActionPreference='Stop';Set-Location -LiteralPath $root;try{$text=(& ([scriptblock]::Create($script)) *>&1 | Out-String);return @{Code=$(if($LASTEXITCODE){$LASTEXITCODE}else{0});Output=$text;Error=''}}catch{return @{Code=1;Output='';Error=$_.Exception.GetType().Name}}} -ArgumentList $s.Script,$root
  if(!(Wait-Job $job -Timeout ([int]$s.TimeoutSeconds))){Stop-Job $job;$code=124;$errorText='Guest script timed out'}else{$jr=Receive-Job $job;$output=[string]$jr.Output;$errorText=[string]$jr.Error;$code=[int]$jr.Code}
  Remove-Job $job -Force
 }catch{$code=1;$errorText=$_.Exception.GetType().Name}
 $items=@{};$total=0
 foreach($relative in $s.Artifacts){
  $path=Join-Path $root $relative
  $cursor=$path
  while($cursor.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){
   if(Test-Path -LiteralPath $cursor){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'SDK artifact reparse point'}}
   if($cursor -eq $root){break};$cursor=Split-Path $cursor
  }
  if(Test-Path -LiteralPath $path -PathType Leaf){$bytes=[IO.File]::ReadAllBytes($path);$total+=$bytes.Length;if($total -gt 1000000){throw 'Artifact bound exceeded'};$items[$relative]=[Convert]::ToBase64String($bytes)}
 }
 $receipt=@{Nonce=$s.Nonce;EnvironmentFingerprint=$s.EnvironmentFingerprint;ExitCode=$code;Stdout=$output.Substring(0,[Math]::Min(8192,$output.Length));Stderr=$errorText.Substring(0,[Math]::Min(8192,$errorText.Length));Artifacts=$items}
 $receipt | ConvertTo-Json -Compress -Depth 5 | Set-Content -LiteralPath 'C:\AgentOS\sdk-receipt.json'
 return $receipt
} -ArgumentList $spec
$r | ConvertTo-Json -Compress -Depth 5 | Set-Content -LiteralPath $m.Result
Stop-VM -VM $vm -Shutdown -Confirm:$false
$deadline=(Get-Date).AddSeconds(45)
while((Get-VM -Id $vm.Id).State -ne 'Off' -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 500}
$vm=Get-VM -Id $vm.Id
$drive=@(Get-VMHardDiskDrive -VM $vm)
if($vm.State -ne 'Off' -or $vm.Notes -ne $m.Nonce -or $vm.Name -ne $m.VmName -or $drive.Count -ne 1 -or $drive[0].Path -ne $m.Disk){throw 'SDK VM shutdown or ownership unconfirmed'}
Remove-VM -VM $vm -Force
if(Get-VM -Id $vm.Id -ErrorAction SilentlyContinue){throw 'SDK VM still exists'}
Remove-Item -LiteralPath $m.Disk -Force
Set-Content -LiteralPath $m.Confirmed -Value $m.Nonce
";
}


