using AgentOS.Core;
using System.Text;
namespace AgentOS.Tests;
internal static class WorkExecutionChecks
{
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 public static Task RunAsync()
 {
  var root=Path.Combine(Path.GetTempPath(),"agentos-sdk-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try
  {
   var workflow=Path.Combine(root,".github","workflows","check.yml");Directory.CreateDirectory(Path.GetDirectoryName(workflow)!);
   File.WriteAllText(workflow,"name: check");File.WriteAllText(Path.Combine(root,"config.toml"),"mode = 'safe'");File.WriteAllText(Path.Combine(root,"input.txt"),"original");
   var manifest=WorkExecution.InputManifest(root);Check(manifest.SequenceEqual(new[]{".github/workflows/check.yml","config.toml","input.txt"}),"Input manifest omitted tracked dotfile or config.");
   var original=WorkExecution.SourceHash(root,manifest);Directory.CreateDirectory(Path.Combine(root,"bin"));File.WriteAllText(Path.Combine(root,"bin","generated.dll"),"output");
   Check(WorkExecution.SourceHash(root,manifest)==original,"Generated output changed frozen input fingerprint.");
   File.WriteAllText(workflow,"name: changed");Check(WorkExecution.SourceHash(root,manifest)!=original,"Workflow mutation was accepted.");File.WriteAllText(workflow,"name: check");
   File.WriteAllText(Path.Combine(root,"config.toml"),"mode = 'changed'");Check(WorkExecution.SourceHash(root,manifest)!=original,"Project config mutation was accepted.");File.WriteAllText(Path.Combine(root,"config.toml"),"mode = 'safe'");
   File.Delete(Path.Combine(root,"input.txt"));var refused=false;try{_ = WorkExecution.SourceHash(root,manifest);}catch(IOException){refused=true;}Check(refused,"Deleted input was accepted.");
   var verified=new SdkGuestResult(WindowsVmState.Completed,0,"fixture",ReceiptSha256:WorkExecution.Hash("receipt"),EnvironmentFingerprint:WorkExecution.Hash("guest"),ShutdownConfirmed:true,TrustedCollector:true,InputSha256:original,PostSourceSha256:original,CommandSha256:WorkExecution.Hash("command"),ProfileSha256:WorkExecution.Hash("profile"),OwnerReceiptSha256:WorkExecution.Hash("owner"),EffectSha256:WorkExecution.Hash("effect"));
   Check(WorkExecution.ValidationResult(verified,original,verified.CommandSha256,verified.ProfileSha256).SourceUnchanged,"Trusted unchanged inputs did not validate.");
   Check(!WorkExecution.ValidationResult(verified with{PostSourceSha256=WorkExecution.Hash("changed")},original,verified.CommandSha256,verified.ProfileSha256).SourceUnchanged,"Modified input validated.");
   Check(!WorkExecution.ValidationResult(verified with{TrustedCollector=false},original,verified.CommandSha256,verified.ProfileSha256).SourceUnchanged,"Untrusted collector validated.");
  }
  finally{Directory.Delete(root,true);}return Task.CompletedTask;
 }
}
