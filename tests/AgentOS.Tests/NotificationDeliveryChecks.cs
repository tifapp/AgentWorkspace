using AgentOS.Core;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Tests;
internal static class NotificationDeliveryChecks
{
 [ModuleInitializer] internal static void Run()
 {var root=Path.Combine(Environment.CurrentDirectory,"agentos-notification-fixture-"+Guid.NewGuid().ToString("N"));try{Exercise(root);}finally{try{Directory.Delete(root,true);}catch(IOException){}}}
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
  File.WriteAllText(Path.Combine(b,"interactions.json"),"{\"Schema\":2,\"Items\":[]}");watcher.Scan();Check(!watcher.IsCurrent(seen[1]),"unsupported journal accepted");center.ConfigureEnabled(false);Check(!new NotificationCenter(Path.Combine(root,"history.json")).Enabled,"preference not durable");
 }
}
