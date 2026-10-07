using AgentOS.Core;
using System.Text;
using System.Text.Json;
namespace AgentOS.Tests;
internal static class ContextArtifactChecks
{
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 static void Refuses(Action run,string why){try{run();}catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or InvalidOperationException){return;}throw new Exception(why);}
 public static async Task RunAsync(string artifactRoot)
 {
  Directory.CreateDirectory(artifactRoot);var root=Path.Combine(artifactRoot,"store");Directory.CreateDirectory(root);var store=new ContextArtifacts(root);var now=DateTimeOffset.UtcNow;
  var text=store.Accept(ContextArtifactKind.Text,Encoding.UTF8.GetBytes("visible accepted text"),"foreground visible text",now);
  var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY2BgYGAAAAAFAAGKM+MAAAAAAElFTkSuQmCC");
  var image=store.Accept(ContextArtifactKind.Png,png,"foreground screenshot [0,0,1,1]",now);
  Check(store.Read(text).SequenceEqual(Encoding.UTF8.GetBytes("visible accepted text"))&&store.Read(image).SequenceEqual(png),"Accepted bytes changed.");
  var payload=ContextTurnPayload.Build("selected task",[text,image],store);using var turn=JsonDocument.Parse(JsonSerializer.Serialize(payload));
  Check(turn.RootElement.GetProperty("Input").GetArrayLength()==2&&turn.RootElement.GetProperty("AdditionalContext").GetProperty(text.Id).GetProperty("kind").GetString()=="untrusted","Accepted context was not sent as untrusted data.");
  Refuses(()=>store.Read(text with{Source="changed"}),"Context citation was rebound.");
  using(var frame=new MemoryStream()){await ProjectBroker.WriteFrame(frame,"{}",CancellationToken.None);frame.Position=0;Check(await ProjectBroker.ReadFrame(frame,CancellationToken.None)=="{}","Bounded frame roundtrip failed.");frame.Position=0;frame.Write(BitConverter.GetBytes(ContextArtifacts.MaxWireBytes+1));frame.Position=0;var oversized=false;try{await ProjectBroker.ReadFrame(frame,CancellationToken.None);}catch(InvalidDataException){oversized=true;}Check(oversized,"Oversized broker frame was admitted.");}
  var broken=(byte[])png.Clone();broken[^5]^=1;Refuses(()=>store.Accept(ContextArtifactKind.Png,broken,"bad",now),"Bad PNG CRC accepted.");
  var project=await PracticeProject.CreateAsync(Path.Combine(artifactRoot,"project"));
  await using var runtime=await ProjectRuntime.OpenInternal(project,Path.Combine(artifactRoot,"state"),new global::ScriptHost(),Path.Combine(artifactRoot,"coordination"));
  runtime.Configure("Write-Output passed");var accepted=new ContextArtifacts(runtime.DataDirectory).Accept(ContextArtifactKind.Text,Encoding.UTF8.GetBytes("scope"),"fixture",now);
  var task=new MapTask{Title="Inspect",Prompt="Write-Output selected",Acceptance="Selected",Selected=true};var id=runtime.SaveDraftMap(new TaskMap{Title="Context map",Tasks=[task],ContextRefs=[accepted]});
  Check(runtime.Snapshot.Work.Count==0,"Draft started work.");
  var removed=runtime.Snapshot.Maps.Single(x=>x.Id==id);removed.ContextRefs.Clear();runtime.SaveDraftMap(removed,removed.Revision);
  var rebound=runtime.Snapshot.Maps.Single(x=>x.Id==id);rebound.ContextRefs.Add(accepted with{Sha256=new string('0',64)});Refuses(()=>runtime.SaveDraftMap(rebound,rebound.Revision),"Removed context identity was rebound.");
  rebound.ContextRefs.Clear();rebound.ContextRefs.Add(accepted);runtime.SaveDraftMap(rebound,rebound.Revision);var started=await runtime.StartSelectedMapTasksAsync(id,[task.Id],runtime.Snapshot.Maps.Single(x=>x.Id==id).Revision);
  Check(runtime.Snapshot.Work.Single(x=>x.Id==started.Single()).ContextRefs.SequenceEqual([accepted]),"Accepted context was not frozen on work.");await runtime.WaitForIdleAsync();
  var legacy=ContextCitation.Create("legacy",Encoding.UTF8.GetBytes("legacy data"));var legacyId=runtime.SaveDraftMap(new TaskMap{Title="Legacy",Tasks=[new MapTask{Title="Inspect",Prompt="Write-Output inspect",Acceptance="Done"}],Citations=[legacy]});
  var oldDraft=runtime.Snapshot.Maps.Single(x=>x.Id==legacyId);oldDraft.Citations.Clear();runtime.SaveDraftMap(oldDraft,oldDraft.Revision);
  var reboundLegacy=runtime.Snapshot.Maps.Single(x=>x.Id==legacyId);reboundLegacy.Citations.Add(legacy with{Sha256=new string('0',64)});Refuses(()=>runtime.SaveDraftMap(reboundLegacy,reboundLegacy.Revision),"Removed legacy citation identity was rebound.");  var bad=new ContextArtifacts(runtime.DataDirectory).Accept(ContextArtifactKind.Text,Encoding.UTF8.GetBytes("tamper"),"fixture",now);var badTask=new MapTask{Title="Tamper",Prompt="Write-Output selected",Acceptance="Selected",Selected=true};var badId=runtime.SaveDraftMap(new TaskMap{Title="Tamper map",Tasks=[badTask],ContextRefs=[bad]});
  File.WriteAllText(Path.Combine(runtime.DataDirectory,"context-artifacts",bad.Sha256),"changed");Refuses(()=>runtime.StartSelectedMapTasksAsync(badId,[badTask.Id],runtime.Snapshot.Maps.Single(x=>x.Id==badId).Revision).GetAwaiter().GetResult(),"Tampered context started.");
 }
}
