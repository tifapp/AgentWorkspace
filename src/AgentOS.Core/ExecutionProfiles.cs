using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
public enum ExecutionBackend { AppContainer, HyperV, WindowsSandbox }
public sealed record HyperVProfile(string BaseVhdPath,string BaseVhdSha256,string GuestCredentialTarget,string SwitchName,string? DestinationCredentialTarget=null,string[]? PinnedDestinationIps=null,string? WorkerCredentialTarget=null);
public sealed record ExecutionProfile(ExecutionBackend SdkBackend=ExecutionBackend.AppContainer,HyperVProfile? HyperV=null);
public sealed record ExecutionProfileDescription(ExecutionProfile Profile,bool Available,string Reason,bool DeploymentAvailable=false,string DeploymentReason="Deployment profile is not configured.");
public sealed class ExecutionProfileRegistry(string projectDataRoot)
{
 readonly string file=Path.Combine(Path.GetFullPath(projectDataRoot),"execution-profile.json");
 public ExecutionProfile Current=>File.Exists(file)?JsonSerializer.Deserialize<ExecutionProfile>(File.ReadAllText(file))??new():new();
 public void Configure(ExecutionProfile profile)
 {
  Validate(profile);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
  var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{File.WriteAllText(temp,JsonSerializer.Serialize(profile));File.Move(temp,file,true);}
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public ExecutionProfileDescription Describe()
 {
  ExecutionProfile p;try{p=Current;Validate(p);}catch(Exception e){return new(new(),false,"Invalid execution profile: "+e.GetType().Name);}
  var deployment=HyperVExecution.Probe(p.HyperV);
  var sdk=p.SdkBackend switch {
   ExecutionBackend.HyperV=>p.HyperV is {} h ? WorkExecution.DescribeAvailability(h) : new HyperVProbe(false,"Hyper-V profile is not configured."),
   ExecutionBackend.WindowsSandbox=>new HyperVProbe(false,"Windows Sandbox SDK unavailable: exact shutdown proof missing."),
   _=>new HyperVProbe(true,"AppContainer selected.")
  };
  return new(p,sdk.Available,sdk.Reason,deployment.Available,deployment.Reason);
 }
 public static void Validate(ExecutionProfile p)
 {
  if(!Enum.IsDefined(p.SdkBackend))throw new ArgumentException("Unknown SDK backend.");
  if(p.SdkBackend==ExecutionBackend.HyperV&&p.HyperV==null)throw new ArgumentException("Hyper-V profile required.");
  if(p.HyperV is not {} h)return;
  if(!Path.IsPathFullyQualified(h.BaseVhdPath)||!new[]{".vhd",".vhdx"}.Contains(Path.GetExtension(h.BaseVhdPath),StringComparer.OrdinalIgnoreCase)||
   !Regex.IsMatch(h.BaseVhdSha256,"^[A-Fa-f0-9]{64}$")||
   !Regex.IsMatch(h.GuestCredentialTarget,"^AgentOS/HyperV/Guest/[A-Za-z0-9._-]{1,100}$")||
   !Regex.IsMatch(h.SwitchName,"^[A-Za-z0-9 _.-]{0,100}$")||
   h.DestinationCredentialTarget is {} t&&!Regex.IsMatch(t,"^AgentOS/HyperV/Destination/[A-Za-z0-9._-]{1,100}$")||h.WorkerCredentialTarget is {} w&&!Regex.IsMatch(w,"^AgentOS/HyperV/Worker/[A-Za-z0-9._-]{1,100}$"))
   throw new ArgumentException("Invalid Hyper-V profile.");
  if(h.PinnedDestinationIps is {Length:>32}||h.PinnedDestinationIps?.Any(x=>!IPAddress.TryParse(x,out var ip)||ip.IsIPv6LinkLocal||IPAddress.IsLoopback(ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any))==true)throw new ArgumentException("Invalid pinned destination IP.");
 }
}