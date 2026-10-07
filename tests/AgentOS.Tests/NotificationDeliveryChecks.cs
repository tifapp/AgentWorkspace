using AgentOS.Core;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Tests;
internal static class NotificationDeliveryChecks
{
 internal static Task RunAsync(string root) {Directory.CreateDirectory(root);Exercise(root);return Task.CompletedTask;}
 static void Check(bool yes,string message){if(!yes)throw new Exception("Notification fixture: "+message);}
 static void Exercise(string root)
 {var projectA=Path.Combine(root,"project-a");var projectB=Path.Combine(root,"project-b");Directory.CreateDirectory(projectA);Directory.CreateDirectory(projectB);var data=Path.Combine(root,"data");
  string Dir(string project){var full=Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..24];var dir=Path.Combine(data,hash);Directory.CreateDirectory(dir);return dir;}
  var a=Dir(projectA);var b=Dir(projectB);
  void State(string dir,string project,params HumanDecision[] decisions)=>File.WriteAllText(Path.Combine(dir,"state.json"),JsonSerializer.Serialize(new ProjectState{ProjectPath=Path.GetFullPath(project),Work=[new WorkUnit{Id="same-work",Status=WorkStatus.Running}],Decisions=decisions.ToList()},JsonFormat.Options));
  void Items(string dir,params TaskInteraction[] items)=>File.WriteAllText(Path.Combine(dir,"interactions.json"),JsonSerializer.Serialize(new{Schema=1,Items=items},JsonFormat.Options));
  State(a,projectA);State(b,projectB);Items(a);Items(b);
  var center=new NotificationCenter(Path.Combine(root,"history.json"));center.ConfigureQuietHours(TimeOnly.FromDateTime(DateTime.Now.AddHours(1)),TimeOnly.FromDateTime(DateTime.Now.AddHours(2)));
  var seen=new List<LocalNotice>();using var watcher=new ProjectNotificationWatcher(center,seen.Add);watcher.Select(projectA,data);
  var request=new TaskInteraction{Id="request-one",WorkId="same-work",Kind=InteractionKind.Clarification,Status=InteractionStatus.Pending,Text="private credential 123"};
  Items(a,request);watcher.Scan();Check(seen.Count==1,"pending clarification not delivered");Check(!seen[0].Title.Contains("credential")&&seen[0].DecisionId==request.Id,"private content leaked or identity lost");
  watcher.Scan();Check(seen.Count==1,"pending request duplicated");Check(watcher.IsCurrent(seen[0]),"live request refused");
  request.Status=InteractionStatus.Replied;Items(a,request);watcher.Scan();Check(!watcher.IsCurrent(seen[0]),"resolved request clickable");
  watcher.Select(projectB,data);Check(!watcher.IsCurrent(seen[0]),"old project request clickable");
  var peer=new TaskInteraction{Id="peer-two",WorkId="origin",TargetWorkId="same-work",Kind=InteractionKind.Peer,Status=InteractionStatus.Pending};Items(b,peer);watcher.Scan();Check(seen.Count==2&&seen[1].WorkId=="same-work","peer target not notified");
  center.ConfigureQuietHours(new TimeOnly(0,0),new TimeOnly(0,0));var followup=new TaskInteraction{Id="followup-three",WorkId="same-work",Kind=InteractionKind.Followup,Status=InteractionStatus.Pending};Items(b,peer,followup);watcher.Scan();Check(seen.Count==2&&center.History.Count==3&&!center.History.Last().Delivered,"quiet request delivery claim");
  center.ConfigureQuietHours(TimeOnly.FromDateTime(DateTime.Now.AddHours(1)),TimeOnly.FromDateTime(DateTime.Now.AddHours(2)));watcher.Scan();Check(seen.Count==2,"quiet request popped later");
  var decision=new HumanDecision{Id="decision-four",WorkId="same-work",Status=DecisionStatus.Pending};State(b,projectB,decision);watcher.Scan();Check(seen.Count==3&&seen.Last().DecisionId==decision.Id,"decision not notified");
  var effects=Path.Combine(b,"external-effects");Directory.CreateDirectory(effects);var scope=new EffectScope("fixture","candidate","evidence","artifact","command","environment","destination","operation","{}");File.WriteAllText(Path.Combine(effects,"intent.json"),JsonSerializer.Serialize(new EffectIntent("effect-five",scope,ExternalEffectState.Prepared,null,null,"",DateTimeOffset.UtcNow),JsonFormat.Options));watcher.Scan();Check(seen.Count==4&&watcher.IsCurrent(seen.Last()),"external review not notified");
  var conflict=new ConflictNotice{Id="conflict-six",WorkId="same-work",Cause="TouchedPathChanged"};
  File.WriteAllText(Path.Combine(b,"state.json"),JsonSerializer.Serialize(new ProjectState{ProjectPath=Path.GetFullPath(projectB),Work=[new WorkUnit{Id="same-work",Status=WorkStatus.NeedsResponse}],Decisions=[decision],Conflicts=[conflict]},JsonFormat.Options));
  watcher.Scan();Check(seen.Count==5&&seen.Last().Identity=="conflict:"+conflict.Id&&watcher.IsCurrent(seen.Last()),"conflict not notified");
  conflict.Response="handled";File.WriteAllText(Path.Combine(b,"state.json"),JsonSerializer.Serialize(new ProjectState{ProjectPath=Path.GetFullPath(projectB),Work=[new WorkUnit{Id="same-work",Status=WorkStatus.Parked}],Decisions=[decision],Conflicts=[conflict]},JsonFormat.Options));
  Check(!watcher.IsCurrent(seen[4]),"answered conflict remained clickable");
  File.WriteAllText(Path.Combine(b,"interactions.json"),"{\"Schema\":2,\"Items\":[]}");watcher.Scan();Check(!watcher.IsCurrent(seen[1]),"unsupported journal accepted");
  // Real state files carry prompt and diff history; notification text must remain fixed metadata.
  var largeProject=Path.Combine(root,"large-project");Directory.CreateDirectory(largeProject);
  var largeDir=Path.Combine(root,"large-state");Directory.CreateDirectory(largeDir);
  var largeSecret="private prompt and diff content";
  var largeDecision=new HumanDecision{Id="large-decision",WorkId="large-work",Status=DecisionStatus.Pending};
  var largeConflict=new ConflictNotice{Id="large-conflict",WorkId="large-work",Cause=largeSecret};
  var largeState=new ProjectState{Schema=3,ProjectPath=Path.GetFullPath(largeProject),
   Work=[new WorkUnit{Id="large-work",Status=WorkStatus.NeedsResponse,Task=largeSecret,Diff=new string('x',8_600_000)+largeSecret}],
   Decisions=[largeDecision],Conflicts=[largeConflict]};
  var largePath=Path.Combine(largeDir,"state.json");
  File.WriteAllText(largePath,JsonSerializer.Serialize(largeState,JsonFormat.Options));
  Check(new FileInfo(largePath).Length>8_000_000,"large state fixture did not exceed previous limit");
  var largeSeen=new List<LocalNotice>();
  using(var largeWatcher=new ProjectNotificationWatcher(center,largeSeen.Add))
  {
   largeWatcher.Select(largeProject,Path.Combine(root,"wrong-data-root"),largeDir);
   Check(largeSeen.Count==2&&largeSeen.Any(x=>x.Identity=="decision:"+largeDecision.Id)&&largeSeen.Any(x=>x.Identity=="conflict:"+largeConflict.Id),"schema 3 large state pending requests not delivered");
   Check(largeSeen.All(x=>!x.Title.Contains(largeSecret,StringComparison.Ordinal)&&x.Title.Length<=100&&largeWatcher.IsCurrent(x)),"private state content leaked or schema 3 notice refused");
   largeWatcher.Scan();
   Check(largeSeen.Count==2&&center.History.Count(x=>x.Project==Path.GetFullPath(largeProject))==2,"large state notifications duplicated");
   largeState.Schema=4;
   largeState.Decisions=[new HumanDecision{Id="future-decision",WorkId="large-work",Status=DecisionStatus.Pending}];
   largeState.Conflicts=[new ConflictNotice{Id="future-conflict",WorkId="large-work"}];
   File.WriteAllText(largePath,JsonSerializer.Serialize(largeState,JsonFormat.Options));
   largeWatcher.Scan();
   Check(largeSeen.Count==2&&largeSeen.All(x=>!largeWatcher.IsCurrent(x)),"unknown future state version was accepted");
   File.WriteAllText(largePath,"{malformed");
   largeWatcher.Scan();
   Check(largeSeen.Count==2&&largeSeen.All(x=>!largeWatcher.IsCurrent(x)),"malformed state was accepted");
   using(var oversized=File.Open(largePath,FileMode.Create,FileAccess.Write,FileShare.None))oversized.SetLength(64_000_001);
   largeWatcher.Scan();
   Check(largeSeen.Count==2&&largeSeen.All(x=>!largeWatcher.IsCurrent(x)),"state above bounded capacity was accepted");
  }
  Parallel.For(0,48,i=>{var other=new NotificationCenter(Path.Combine(root,"history.json"));other.Record("parallel:"+i,projectB,"same-work",null,"Test","Concurrent history");other.Record("shared",projectB,"same-work",null,"Test","Deduplicated history");});
  Check(center.History.Count(x=>x.Identity.StartsWith("parallel:",StringComparison.Ordinal))==48&&center.History.Count(x=>x.Identity=="shared")==1,"concurrent history lost or duplicated");
  center.ConfigureEnabled(false);Check(!new NotificationCenter(Path.Combine(root,"history.json")).Enabled,"preference not durable");
 }
}




