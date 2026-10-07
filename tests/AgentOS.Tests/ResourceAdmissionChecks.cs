using AgentOS.Core;
namespace AgentOS.Tests;
internal static class ResourceAdmissionChecks
{
 public static async Task RunAsync()
 {
  var root=Path.Combine(Path.GetTempPath(),"agentos-admission-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root); var child=Path.Combine(root,"Child"); Directory.CreateDirectory(child);
  var file=Path.Combine(child,"item.txt"); File.WriteAllText(file,"x");
  try {
   var parent=ResourceAdmission.Directory(root,ResourceAccess.Write);
   var nested=ResourceAdmission.File(file,ResourceAccess.Read);
   if(!ResourceAdmission.Conflicts(parent,nested)) throw new Exception("Directory overlap was missed.");
   var upper=ResourceAdmission.File(file.ToUpperInvariant(),ResourceAccess.Write);
   if(nested.FileIdentity!=upper.FileIdentity) throw new Exception("Case alias identity mismatch.");
   if(ResourceAdmission.Conflicts(ResourceAdmission.File(file,ResourceAccess.Read),nested)) throw new Exception("Readers should coexist.");
   var second=Path.Combine(child,"second.txt"); File.WriteAllText(second,"y");
   try { GitSnapshotPreconditions.ValidateLinkTarget(root,"Child/link","../../outside"); throw new Exception("Escaping link was admitted."); } catch(IOException) { }
   var coordinator=new MachineCoordinator(Path.Combine(root,"coordination"));
   using var held=await coordinator.EnterAsync(new[]{nested with { Access=ResourceAccess.Write }}, "hold",null,CancellationToken.None);
   using var canceled=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
   try { using var blocked=await coordinator.EnterAsync(new[]{upper}, "cancel",null,canceled.Token); throw new Exception("Cancellation did not remove waiter."); }
   catch(OperationCanceledException) { }
   if(coordinator.Snapshot().Count!=1) throw new Exception("Canceled request remained queued.");
   var secondClaim=ResourceAdmission.File(second,ResourceAccess.Write);
   var pending=coordinator.EnterAsync(new[]{upper,secondClaim},"multi",null,CancellationToken.None);
   await Task.Delay(100); if(pending.IsCompleted) throw new Exception("Multi-resource request bypassed held file.");
   held.Dispose(); using var both=await pending.WaitAsync(TimeSpan.FromSeconds(5));
   if(coordinator.Snapshot().Count!=1) throw new Exception("Atomic request was not represented as one admission.");
   if(new WindowsVmExecution().RunAsync(root,new("dotnet",[]),null,CancellationToken.None).GetAwaiter().GetResult().State!=WindowsVmState.Unavailable && !WindowsVmExecution.Available)
    throw new Exception("Unavailable Sandbox must fail closed.");
   var profile=WindowsVmExecution.Profile(root);
   if(!profile.Contains("<Networking>Disable</Networking>",StringComparison.Ordinal)||!profile.Contains("<ClipboardRedirection>Disable</ClipboardRedirection>",StringComparison.Ordinal))
    throw new Exception("Sandbox profile lost confinement settings.");
  } finally { try { Directory.Delete(root,true); } catch {} }
 }
}


