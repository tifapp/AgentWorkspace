using AgentOS.Core;
using System.Text.Json;
namespace AgentOS.Tests;
internal static class WorkExecutionAdversarialChecks
{
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 public static async Task RunAsync()
 {
  var root=Path.Combine(Path.GetTempPath(),"agentos-sdk-adversarial-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var priorRunner=WorkExecution.Runner;var priorAvailability=WorkExecution.Availability;var priorWorker=WorkExecution.WorkerReady;var priorRecoverer=WorkExecution.Recoverer;
  try
  {
   var image=Path.Combine(root,"base.vhdx");await File.WriteAllBytesAsync(image,[1,2,3]);var hyper=new HyperVProfile(image,StateStore.HashFile(image),"AgentOS/HyperV/Guest/test","",WorkerCredentialTarget:"AgentOS/HyperV/Worker/test");
   var source=Path.Combine(root,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"input.txt"),"original");File.WriteAllText(Path.Combine(source,"auth.json"),"secret");
   WorkUnit Work(){var json=JsonSerializer.Serialize(new ExecutionProfile(ExecutionBackend.HyperV,hyper));return new WorkUnit{Workspace=source,ExecutionProfileSnapshot=json,ExecutionProfileSha256=WorkExecution.Hash(json)};}
   WorkExecution.Availability=_=>new HyperVProbe(true,"fixture");WorkExecution.WorkerReady=_=>true;var selected=Work();
   var expected=WorkExecution.SourceHash(source,["input.txt"]);var command=WorkExecution.Hash("Write-Output ok");var profileHash=WorkExecution.Hash(JsonSerializer.Serialize(hyper));
   WorkExecution.Runner=(_,request,_)=>{Check(!File.Exists(Path.Combine(request.PrivateWorkspace,"auth.json"))&&File.Exists(Path.Combine(request.PrivateWorkspace,"input.txt")),"Credential copied or input lost.");File.WriteAllText(Path.Combine(request.PrivateWorkspace,"output.txt"),"guest");File.WriteAllText(Path.Combine(request.PrivateWorkspace,"unrequested.txt"),"private");return Task.FromResult(new SdkGuestResult(WindowsVmState.Completed,0,"fixture",ReceiptSha256:WorkExecution.Hash("receipt"),EnvironmentFingerprint:WorkExecution.Hash("ENV"),ShutdownConfirmed:true,TrustedCollector:true,InputSha256:expected,PostSourceSha256:expected,CommandSha256:command,ProfileSha256:profileHash,OwnerReceiptSha256:WorkExecution.Hash("OWNER"),EffectSha256:WorkExecution.Hash("EFFECT")));};
   var success=await WorkExecution.RunSdkAsync(selected,root,"Write-Output ok",["output.txt"],5,CancellationToken.None);Check(WorkExecution.ShutdownConfirmed(success)&&File.Exists(Path.Combine(source,"output.txt"))&&!File.Exists(Path.Combine(source,"unrequested.txt")),"Requested export failed or unrequested output escaped.");
   var receipt=Directory.EnumerateFiles(Path.Combine(root,"sdk-runs",selected.Id),"receipt-*.json").Single();Check(File.ReadAllText(receipt+".sha256")==WorkExecution.Hash(File.ReadAllText(receipt)),"Receipt hash wrong.");
   var untrusted=Work();WorkExecution.Runner=(_,request,_)=>{File.WriteAllText(Path.Combine(request.PrivateWorkspace,"forged.txt"),"forged");return Task.FromResult(new SdkGuestResult(WindowsVmState.Completed,0,"claimed",ShutdownConfirmed:true));};
   await WorkExecution.RunSdkAsync(untrusted,root,"Write-Output ok",["forged.txt"],5,CancellationToken.None);Check(!File.Exists(Path.Combine(source,"forged.txt"))&&WorkExecution.HasUnknownOwnership(root,untrusted.Id),"Malformed proof exported or released owner.");
   File.Delete(Path.Combine(source,"output.txt"));var unknownLaunches=0;WorkExecution.Runner=(_,_,_)=>{unknownLaunches++;return Task.FromResult(new SdkGuestResult(WindowsVmState.Unknown,null,"unconfirmed"));};var unknown=await WorkExecution.RunSdkAsync(selected,root,"Write-Output ok",["output.txt"],5,CancellationToken.None);WorkExecution.ReconcileOwnership(root);
   Check(!WorkExecution.ShutdownConfirmed(unknown)&&WorkExecution.HasUnknownOwnership(root,selected.Id)&&!File.Exists(Path.Combine(source,"output.txt")),"Unconfirmed shutdown exported output.");
   try{await WorkExecution.RunSdkAsync(selected,root,"Write-Output ok",[],5,CancellationToken.None);throw new Exception("Shutdown debt bypassed.");}catch(IOException){}
   var operation=WorkExecution.PendingOperationId(root,selected.Id)??throw new Exception("Operation ID missing.");var ownerRoot=Path.Combine(root,"sdk-runs",selected.Id);var stage=Path.Combine(ownerRoot,operation);var owned=Path.Combine(stage,".git","agent-os-temp","owned");Directory.CreateDirectory(owned);
   var inputs=WorkExecution.InputManifest(stage).ToArray();var manifest=new SdkManifest("AgentOS-SDK-owned",Path.Combine(owned,"child.vhdx"),hyper.BaseVhdPath,hyper.BaseVhdSha256,"nonce","creator","Write-Output ok",5,[],[],Path.Combine(owned,"result.json"),Path.Combine(owned,"shutdown-confirmed"),"HOST",WorkExecution.SourceHash(stage,inputs),command,profileHash,"agentos-sdk-collector-v2",inputs);
   File.WriteAllText(Path.Combine(owned,"manifest.json"),JsonSerializer.Serialize(manifest));var vmId=Guid.NewGuid();var ownerFile=Path.Combine(owned,"owner.json");var exactOwner=JsonSerializer.Serialize(new{VmId=vmId,Nonce=manifest.Nonce,Name=manifest.VmName,Disk=manifest.Disk,Creator=manifest.Creator});File.WriteAllText(ownerFile,exactOwner);File.WriteAllText(manifest.Result,"{\"Artifacts\":{}}");
   File.WriteAllText(ownerFile,JsonSerializer.Serialize(new{VmId=vmId,Nonce="wrong",Name=manifest.VmName,Disk=manifest.Disk,Creator=manifest.Creator}));try{await WorkExecution.ReconcileAsync(selected,root,operation,CancellationToken.None);throw new Exception("Wrong owner nonce admitted.");}catch(InvalidDataException){}File.WriteAllText(ownerFile,exactOwner);
   var recoveryCalls=0;WorkExecution.Recoverer=(_,_,_)=>{recoveryCalls++;return Task.FromResult(new SdkGuestResult(WindowsVmState.Unknown,null,"still running"));};var pending=await WorkExecution.ReconcileAsync(selected,root,operation,CancellationToken.None);Check(!pending.ShutdownConfirmed&&WorkExecution.HasUnknownOwnership(root,selected.Id),"Unproven recovery released VM.");
   var receiptHash=StateStore.HashFile(manifest.Result);var ownerHash=WorkExecution.Hash(manifest.Nonce+"\n"+manifest.VmName+"\n"+vmId.ToString("D")+"\n"+manifest.Disk+"\n"+receiptHash);
   WorkExecution.Recoverer=(_,_,_)=>{recoveryCalls++;return Task.FromResult(new SdkGuestResult(WindowsVmState.Completed,0,"exact",ReceiptSha256:receiptHash,EnvironmentFingerprint:WorkExecution.Hash("ENV"),ShutdownConfirmed:true,TrustedCollector:true,InputSha256:manifest.InputSha256,PostSourceSha256:manifest.InputSha256,CommandSha256:command,ProfileSha256:profileHash,OwnerReceiptSha256:ownerHash,EffectSha256:WorkExecution.Hash("{}")));};
   var recovered=await WorkExecution.ReconcileAsync(selected,root,operation,CancellationToken.None);Check(recoveryCalls==2&&unknownLaunches==1&&recovered.TrustedCollector&&!WorkExecution.HasUnknownOwnership(root,selected.Id),"Exact recovery replayed script or did not release ownership.");
   Check(Directory.EnumerateFiles(ownerRoot,"resolved-*.json").Any()&&Directory.EnumerateFiles(ownerRoot,"unknown-*.json").Any()&&Directory.EnumerateFiles(ownerRoot,"reconciled-*.json").Count()==2,"Historical ownership records lost.");
  }
  finally{WorkExecution.Runner=priorRunner;WorkExecution.Availability=priorAvailability;WorkExecution.WorkerReady=priorWorker;WorkExecution.Recoverer=priorRecoverer;try{Directory.Delete(root,true);}catch(IOException){}}
 }
}
