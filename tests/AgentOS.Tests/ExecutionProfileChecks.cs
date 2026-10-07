using AgentOS.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Tests;
public static class ExecutionProfileChecks
{
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static void Reject(Action action,string message){try{action();}catch(ArgumentException){return;}throw new Exception(message);}
 static string H(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
 public static Task RunAsync()
 {
  var root=Path.Combine(Path.GetTempPath(),"agentos-profile-checks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try
  {
   var registry=new ExecutionProfileRegistry(root);
   Check(registry.Current.SdkBackend==ExecutionBackend.AppContainer&&registry.Describe().Available&&!registry.Describe().DeploymentAvailable,"AppContainer must be default; deployment requires configuration.");
   var basePath=Path.Combine(root,"base.vhdx");
   var profile=new HyperVProfile(basePath,new string('A',64),"AgentOS/HyperV/Guest/test","private-switch",null,["192.0.2.10"]);
   registry.Configure(new(ExecutionBackend.HyperV,profile));
   Check(registry.Current.HyperV?.BaseVhdSha256==profile.BaseVhdSha256,"Profile did not persist.");
   Check(!registry.Describe().Available,"Missing base image or platform must be unavailable.");
   File.WriteAllText(basePath,"fixture VHD hash only; never booted");
   Check(HyperVExecution.Probe(profile).Reason=="Base VHD hash changed.","Changed base image hash was not refused.");
   Check(!File.ReadAllText(Path.Combine(root,"execution-profile.json")).Contains("password",StringComparison.OrdinalIgnoreCase),"Profile should contain only credential targets.");
   Reject(()=>registry.Configure(new(ExecutionBackend.HyperV,profile with{GuestCredentialTarget="raw-secret"})),"Raw credential accepted.");
   Reject(()=>registry.Configure(new(ExecutionBackend.HyperV,profile with{BaseVhdSha256="not-a-hash"})),"Invalid base image hash accepted.");
   Reject(()=>registry.Configure(new(ExecutionBackend.HyperV,profile with{PinnedDestinationIps=["127.0.0.1"]})),"Loopback pin accepted.");
   Reject(()=>HyperVExecution.EgressManifest("http://example.test",["192.0.2.10"]),"Non-HTTPS destination accepted.");
   var acl=HyperVExecution.EgressManifest("https://example.test",["192.0.2.10"]);
   Check(acl.SequenceEqual(new[]{"deny|0.0.0.0/0|any","deny|::/0|any","allow|192.0.2.10|TCP:443"}),"Default-deny ACL manifest changed.");
   var effect=new DeploymentEffect("hyperv",new string('A',64),new string('B',64),new string('C',64),"https://example.test");
   var scope=new EffectScope("deployment",new string('a',40),new string('D',64),effect.ArtifactSha256,effect.CommandSha256,effect.EnvironmentSha256,effect.Destination,"deploy",JsonSerializer.Serialize(effect));
   var receipt=new HyperVReceipt("op","control-nonce",scope.Digest,scope.EvidenceSha256,effect.ArtifactSha256,effect.CommandSha256,effect.EnvironmentSha256,H(effect.Destination),0,"ok","");
   Check(HyperVExecution.ValidReceipt(receipt,"op","control-nonce",scope),"Exact receipt refused.");
   Check(!HyperVExecution.ValidReceipt(receipt with{CommandSha256=new string('D',64)},"op","control-nonce",scope),"Command hash tamper accepted.");
   Check(!HyperVExecution.ValidReceipt(receipt with{OperationId="other"},"op","control-nonce",scope),"Operation identity tamper accepted.");
   Check(!HyperVExecution.ValidReceipt(receipt with{DestinationSha256=new string('F',64)},"op","control-nonce",scope),"Destination hash tamper accepted.");
   Check(!HyperVExecution.ValidReceipt(receipt with{ControlNonce="forged"},"op","control-nonce",scope),"Control nonce tamper accepted.");
   Check(!HyperVExecution.ValidReceipt(receipt,"op","control-nonce",scope with{EvidenceSha256=new string('E',64)}),"Source evidence tamper accepted.");
   Check(!HyperVExecution.ValidReceipt(receipt with{Stdout=new string('x',8193)},"op","control-nonce",scope),"Unbounded output accepted.");
   Reject(()=>HyperVSdkRunner.SafeRelative(".git/config"),"SDK artifact could overwrite Git metadata.");
   Reject(()=>HyperVSdkRunner.SafeRelative("../escape"),"SDK artifact traversal accepted.");
   Reject(()=>HyperVSdkRunner.SafeRelative("artifact.txt:stream"),"SDK artifact alternate stream accepted.");
   Reject(()=>HyperVSdkRunner.SafeRelative("CON.txt"),"SDK artifact Windows device path accepted.");
  }
  finally{Directory.Delete(root,true);}
  return Task.CompletedTask;
 }
}
