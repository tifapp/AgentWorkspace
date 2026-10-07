using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
namespace AgentOS.Core;
public enum WindowsVmState { Unavailable, Completed, Failed, Unknown }
public sealed record WindowsVmResult(WindowsVmState State,int? ExitCode,string Output,string Error,string Evidence);
public sealed record WindowsVmCommand(string Program,IReadOnlyList<string> Arguments,int TimeoutSeconds=300);
public sealed class WindowsVmExecution
{
 public static string SandboxExecutable=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsSandbox.exe");
 public static bool Available=>OperatingSystem.IsWindows()&&File.Exists(SandboxExecutable)&&
  File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"vmcompute.exe"));
 public static string Profile(string privateRoot,string? toolchain=null) {
  var mappings=new XElement("MappedFolders",new XElement("MappedFolder",new XElement("HostFolder",privateRoot),
   new XElement("SandboxFolder",@"C:\AgentOS"),new XElement("ReadOnly","false")));
  if(toolchain!=null) mappings.Add(new XElement("MappedFolder",new XElement("HostFolder",toolchain),
   new XElement("SandboxFolder",@"C:\Toolchain"),new XElement("ReadOnly","true")));
  return new XDocument(new XElement("Configuration",new XElement("VGpu","Disable"),
   new XElement("Networking","Disable"),new XElement("MappedFolders",mappings),
   new XElement("LogonCommand",new XElement("Command",
    "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "+
    Convert.ToBase64String(Encoding.Unicode.GetBytes(@"& 'C:\AgentOS\control\bootstrap.ps1'")))),
   new XElement("AudioInput","Disable"),new XElement("VideoInput","Disable"),
   new XElement("ProtectedClient","Enable"),new XElement("PrinterRedirection","Disable"),
   new XElement("ClipboardRedirection","Disable"))).ToString();
 }
 public static void ValidateTree(string root) {
  ResourceAdmission.Directory(root,ResourceAccess.Write);
  var pending=new Stack<string>(); pending.Push(root);
  while(pending.Count>0) {
   foreach(var entry in Directory.EnumerateFileSystemEntries(pending.Pop())) {
    if(Directory.Exists(entry)) { ResourceAdmission.Directory(entry,ResourceAccess.Read); pending.Push(entry); }
    else {
     ResourceAdmission.File(entry,ResourceAccess.Read);
     using var h=File.OpenHandle(entry,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
     if(!GetInfo(h,out var i)||i.Links!=1) throw new IOException("Mapped tree contains a hard link or unresolved file identity.");
    }
   }
  }
 } public async Task<WindowsVmResult> RunAsync(string privateRoot,WindowsVmCommand command,string? readOnlyToolchain,CancellationToken cancel) {
  if(!Available) return new(WindowsVmState.Unavailable,null,"","","Windows Sandbox platform unavailable.");
  var root=ResourceAdmission.Canonical(privateRoot,true);
  if(readOnlyToolchain!=null) { var tools=ResourceAdmission.Canonical(readOnlyToolchain,true);
   if(tools.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase)||root.StartsWith(tools+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("Toolchain overlaps writable workspace.");
   ValidateTree(tools);
  }
  if(readOnlyToolchain==null) return new(WindowsVmState.Unavailable,null,"","","Read-only SDK toolchain is required.");
  Directory.CreateDirectory(Path.Combine(root,"workspace"));
  ValidateTree(root);
  if(command.TimeoutSeconds<1||command.TimeoutSeconds>3600||command.Arguments.Count>128||
   command.Arguments.Any(x=>x.Length>4096)||command.Program.Contains('/')||command.Program.Contains('\\')||
   !new[]{"dotnet","git","node","npm","python"}.Contains(command.Program,StringComparer.OrdinalIgnoreCase))
   throw new ArgumentException("Command manifest is outside the supported SDK allowlist.");
  var control=Path.Combine(root,"control"); if(Directory.Exists(control)) throw new IOException("VM root already has a run or unresolved ownership record."); Directory.CreateDirectory(control);
  var operation=Guid.NewGuid().ToString("N");
  var manifest=new { Operation=operation,command.Program,command.Arguments,command.TimeoutSeconds };
  File.WriteAllText(Path.Combine(control,"command.json"),JsonSerializer.Serialize(manifest));
  File.WriteAllText(Path.Combine(control,"bootstrap.ps1"),Bootstrap);
  var profilePath=Path.Combine(control,"sandbox.wsb"); File.WriteAllText(profilePath,Profile(root,readOnlyToolchain));
  ValidateTree(root);
  var evidence=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(control,"command.json"))));
  using var launcher=new Process{StartInfo=new ProcessStartInfo(SandboxExecutable){UseShellExecute=true,Arguments="\""+profilePath+"\""}};
  if(!launcher.Start()) return new(WindowsVmState.Unavailable,null,"","",evidence);
  using var stopped=cancel.Register(()=>{try { if(!launcher.HasExited) launcher.Kill(); } catch {}});
  var receipt=Path.Combine(control,"result.json"); var deadline=DateTime.UtcNow.AddSeconds(command.TimeoutSeconds+90);
  while(DateTime.UtcNow<deadline) {
   if(cancel.IsCancellationRequested) { File.WriteAllText(Path.Combine(control,"unknown-ownership.json"),JsonSerializer.Serialize(new { Operation=operation,LauncherPid=launcher.Id,Reason="Canceled before shutdown confirmation" })); return new(WindowsVmState.Unknown,null,"","Cancellation requested; guest shutdown cannot be proven.",evidence); }
   if(File.Exists(receipt)) {
    if(new FileInfo(receipt).Length>1_100_000) throw new InvalidDataException("VM result exceeds bound.");
    var bytes=File.ReadAllBytes(receipt);
    using var doc=JsonDocument.Parse(bytes); var value=doc.RootElement;
    if(value.GetProperty("Operation").GetString()!=operation) throw new InvalidDataException("VM receipt identity mismatch.");
    var exit=value.GetProperty("ExitCode").GetInt32();
    if(launcher.HasExited) { File.WriteAllText(Path.Combine(control,"unknown-ownership.json"),JsonSerializer.Serialize(new { Operation=operation,LauncherPid=launcher.Id,Reason="Launcher detached before guest shutdown confirmation" })); return new(WindowsVmState.Unknown,exit,"","Guest receipt arrived after launcher exit; sandbox ownership unconfirmed.",evidence); }
    using var shutdownWait=new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await launcher.WaitForExitAsync(shutdownWait.Token); }
    catch (OperationCanceledException) { File.WriteAllText(Path.Combine(control,"unknown-ownership.json"),JsonSerializer.Serialize(new { Operation=operation,LauncherPid=launcher.Id,Reason="Shutdown not confirmed" })); return new(WindowsVmState.Unknown,exit,"","Guest command finished; sandbox shutdown unconfirmed.",evidence); }
    if(cancel.IsCancellationRequested) { File.WriteAllText(Path.Combine(control,"unknown-ownership.json"),JsonSerializer.Serialize(new { Operation=operation,LauncherPid=launcher.Id,Reason="Canceled during shutdown" })); return new(WindowsVmState.Unknown,exit,"","Sandbox shutdown after cancellation is unconfirmed.",evidence); }
    return new(exit==0?WindowsVmState.Completed:WindowsVmState.Failed,exit,value.GetProperty("Output").GetString()??"",value.GetProperty("Error").GetString()??"",evidence);
   }
   await Task.Delay(250,CancellationToken.None);
  }
  File.WriteAllText(Path.Combine(control,"unknown-ownership.json"),JsonSerializer.Serialize(new { Operation=operation,LauncherPid=launcher.Id,Reason="Timed out before receipt" }));
  return new(WindowsVmState.Unknown,null,"","VM completion or shutdown unconfirmed.",evidence);
 }
 private const string Bootstrap=@"$ErrorActionPreference='Stop'
$root='C:\AgentOS'
$spec=Get-Content -LiteralPath (Join-Path $root 'control\command.json') -Raw | ConvertFrom-Json
$env:TEMP=Join-Path $root 'cache'; $env:TMP=$env:TEMP; $env:USERPROFILE=$env:TEMP
$env:DOTNET_CLI_HOME=$env:TEMP; $env:NUGET_PACKAGES=Join-Path $env:TEMP 'nuget'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$allowed=@('dotnet','git','node','npm','python')
if($spec.Program -notin $allowed) { throw 'Program denied' }
$exe=Join-Path 'C:\Toolchain' ($spec.Program+'.exe')
if(!(Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Toolchain executable unavailable'}
$out=Join-Path $root 'control\stdout.txt'; $err=Join-Path $root 'control\stderr.txt'
$p=Start-Process -FilePath $exe -ArgumentList @($spec.Arguments) -WorkingDirectory (Join-Path $root 'workspace') -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
if(!$p.WaitForExit([int]$spec.TimeoutSeconds*1000)){ $p.Kill(); $p.WaitForExit(); $exitCode=124 } else { $exitCode=$p.ExitCode }
$result=@{Operation=$spec.Operation;ExitCode=$exitCode;Output=(Get-Content $out -Raw -ErrorAction SilentlyContinue);Error=(Get-Content $err -Raw -ErrorAction SilentlyContinue)}
$result | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $root 'control\result.next')
Move-Item -LiteralPath (Join-Path $root 'control\result.next') -Destination (Join-Path $root 'control\result.json')
shutdown.exe /s /t 0
";
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Info {public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandle",SetLastError=true)] private static extern bool GetInfo(Microsoft.Win32.SafeHandles.SafeFileHandle h,out Info i);
}









