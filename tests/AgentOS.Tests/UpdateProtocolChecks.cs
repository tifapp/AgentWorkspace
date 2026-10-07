using AgentOS.Core;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
internal static class UpdateProtocolChecks
{
 public static async Task Run(Func<string,Func<Task>,Task> test,Func<Task<ProjectRuntime>> runtime,string artifactsRoot)
 {
  await test("Updater rejects malformed trust and unsafe output",async()=>
  {
   var result=await Commands.RunAsync(Commands.PowerShell,["-NoProfile","-NonInteractive","-File",Script("update-agentos.ps1"),"-Package","missing.msix","-Manifest","missing.json","-InstallRoot",".","-Project",".","-CliPath","missing.exe","-TrustedSignerThumbprint","00","-VerifyOnly"],artifactsRoot);
   if(result.ExitCode==0||!(result.Output+result.Error).Contains("trusted signer thumbprint is invalid"))throw new Exception("Malformed trust was accepted.");
   var repo=Directory.GetParent(Path.GetDirectoryName(Script("package-signed.ps1"))!)!.FullName;
   var unsafeOutput=await Commands.RunAsync(Commands.PowerShell,["-NoProfile","-NonInteractive","-File",Script("package-signed.ps1"),"-ArtifactsOutput",repo,"-UnsignedDiagnostic"],artifactsRoot);
   if(unsafeOutput.ExitCode==0||!(unsafeOutput.Output+unsafeOutput.Error).Contains("ArtifactsOutput overlaps"))throw new Exception("Unsafe package output was accepted.");
  });
  await test("Unsigned package cannot request runtime drain",async()=>
  {
   await using var r=await runtime();var path=r.Snapshot.ProjectPath;var file=Path.Combine(artifactsRoot,"unsigned-"+Guid.NewGuid().ToString("N")+".msix");
   var binary=new byte[]{1,2,3,4};var hash=Convert.ToHexString(SHA256.HashData(binary));
   using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))
   {
    using(var writer=new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()))await writer.WriteAsync("<Package><Identity Name=\"AgentOS.Desktop\" Publisher=\"CN=Test\" Version=\"2.0.0.0\" ProcessorArchitecture=\"x64\" /></Package>");
    using(var writer=new StreamWriter(zip.CreateEntry("update-protocol.json").Open()))await writer.WriteAsync(JsonSerializer.Serialize(new{Protocol=1,StateSchema=3,AppBinarySha256=hash,BinarySha256=hash}));
    using(var stream=zip.CreateEntry("AgentOS.Core.dll").Open())await stream.WriteAsync(binary);
    using(var stream=zip.CreateEntry("cli/AgentOS.Core.dll").Open())await stream.WriteAsync(binary);
   }
   var scope=new UpdateScope(StateStore.HashFile(file),"2.0.0.0","x64",artifactsRoot,path,file,new string('B',40));
   await ExpectAsync<UnauthorizedAccessException>(()=>r.BeginUpdateDrainAsync(scope));
  });  await test("Signed update version and rollback guards",()=>
  {
   if(!RuntimeUpdate.IsNewerVersion("2.0.0.0","1.9.9.9")||RuntimeUpdate.IsNewerVersion("1.0.0.0","1.0.0.0"))throw new Exception("Version ordering broken.");
   var health=RuntimeUpdate.Health();if(health.Version!=1||health.StateSchema!=3||health.AssemblySha256!=StateStore.HashFile(typeof(ProjectRuntime).Assembly.Location)||!health.Capabilities.Contains("cooperative-exit")||!health.Capabilities.Contains("schema3"))throw new Exception("Handshake incomplete.");
   if(!RuntimeUpdate.CanRollback(10,3,10,3,true,true)||RuntimeUpdate.CanRollback(10,3,11,3,true,true)||RuntimeUpdate.CanRollback(10,3,10,4,true,true)||RuntimeUpdate.CanRollback(10,3,10,3,false,true)||RuntimeUpdate.CanRollback(10,3,10,3,true,false))throw new Exception("Rollback guard broken.");
   return Task.CompletedTask;
  });
  await test("Drain survives owner restart and refuses new work",async()=>
  {
   var first=await runtime();var path=first.Snapshot.ProjectPath;var dataRoot=Directory.GetParent(first.DataDirectory)!.FullName;
   var metadata=Path.Combine(artifactsRoot,"package-manifest.json");await File.WriteAllTextAsync(metadata,"{\"Version\":\"1.0.0.0\"}");
   var scope=new UpdateScope(new string('A',64),"2.0.0.0","x64",artifactsRoot,path,Path.Combine(artifactsRoot,"candidate.msix"),new string('B',40));
   var token=first.BeginUpdateDrainVerified(scope).Token;await first.DisposeAsync();
   await using var reopened=await ProjectRuntime.OpenInternal(path,dataRoot,new ScriptHost(),Path.Combine(dataRoot,"coordination"));
   if(reopened.UpdateReady(token).Protocol.Version!=1)throw new Exception("Restored drain protocol differs.");
   Expect<InvalidOperationException>(()=>reopened.StartAsync("new work").GetAwaiter().GetResult());
  });
  await test("Drain blockers and map cancellation",async()=>
  {
   await using var r=await runtime();r.Configure("Write-Output passed");var path=r.Snapshot.ProjectPath;
   var scope=new UpdateScope(new string('A',64),"2.0.0.0","x64",artifactsRoot,path,Path.Combine(artifactsRoot,"candidate.msix"),new string('B',40));
   var drain=r.BeginUpdateDrainVerified(scope);
   Expect<InvalidOperationException>(()=>r.StartSelectedMapTasksAsync("missing").GetAwaiter().GetResult());
   await ExpectAsync<InvalidOperationException>(()=>r.ExecuteExternalEffectAsync("missing"));
   await ExpectAsync<InvalidOperationException>(()=>r.DecideAsync("missing",true));
   var state=(ProjectState)typeof(ProjectRuntime).GetField("_state",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(r)!;
   var map=new TaskMap{ProjectPath=path,Status=MapStatus.Active,Tasks=[new MapTask{Title="next",Prompt="next",Acceptance="done",Selected=true}]};state.Maps.Add(map);
   if(!r.UpdateReady(drain.Token).Blockers.Contains("pending-map-work"))throw new Exception("Pending map was ready.");
   r.CancelPendingMapForUpdate(map.Id);
   if(r.UpdateReady(drain.Token).Blockers.Contains("pending-map-work"))throw new Exception("Map cancellation failed.");
   var unknown=new WorkUnit{Task="unconfirmed",Status=WorkStatus.Unknown};state.Work.Add(unknown);
   if(r.UpdateReady(drain.Token).Ready||!r.UpdateReady(drain.Token).Blockers.Contains("unknown-outcome"))throw new Exception("Unknown outcome was ready.");
   Expect<UnauthorizedAccessException>(()=>r.UpdateReady(new string('0',64)));
  });
 }
 private static string Script(string name){for(DirectoryInfo? d=new(Environment.CurrentDirectory);d!=null;d=d.Parent){var p=Path.Combine(d.FullName,"scripts",name);if(File.Exists(p))return p;}throw new FileNotFoundException(name);}
 private static void Expect<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
 private static async Task ExpectAsync<T>(Func<Task> action)where T:Exception{try{await action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
}
