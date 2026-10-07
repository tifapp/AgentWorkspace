using AgentOS.Core;
using System.Reflection;
namespace AgentOS.Tests;
internal static class MapInteractionChecks
{
 static ProjectState State(ProjectRuntime r)=>(ProjectState)typeof(ProjectRuntime).GetField("_state",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(r)!;
 static TaskInteractionStore Store(ProjectRuntime r)=>(TaskInteractionStore)typeof(ProjectRuntime).GetField("_interactions",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(r)!;
 static void Refresh(ProjectRuntime r)=>typeof(ProjectRuntime).GetMethod("UpdateMapStatuses",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(r,null);
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 public static async Task RunAsync(string artifactRoot)
 {
  Directory.CreateDirectory(artifactRoot);var project=await PracticeProject.CreateAsync(artifactRoot);
  await using var r=await ProjectRuntime.OpenInternal(project,Path.Combine(artifactRoot,"state"),new global::ScriptHost(),Path.Combine(artifactRoot,"coordination"));r.Configure("Write-Output passed");
  var a=new MapTask{Title="A",Prompt="Write-Output A",Acceptance="A integrated"};var b=new MapTask{Title="B",Prompt="Write-Output B",Acceptance="B integrated"};
  var id=r.SaveDraftMap(new TaskMap{Title="Partial",Tasks=[a,b]});var map=State(r).Maps.Single(x=>x.Id==id);var preview=r.Snapshot.Maps.Single(x=>x.Id==id);preview.Tasks[1].Selected=true;Check(!map.Tasks[1].Selected,"Preview changed saved selection.");
  var old=map.Revision;await r.StartSelectedMapTasksAsync(id,[a.Id],old);await r.WaitForIdleAsync();Check(map.Tasks[1].WorkId==null&&map.Status==MapStatus.Active,"Partial map started another task or closed.");
  var stale=false;try{await r.StartSelectedMapTasksAsync(id,[b.Id],old);}catch(InvalidOperationException){stale=true;}Check(stale&&map.Tasks[1].WorkId==null,"Stale revision authorized work.");
  await r.StartSelectedMapTasksAsync(id,[b.Id],map.Revision);await r.WaitForIdleAsync();Check(map.Status==MapStatus.Completed,"Later selected node did not complete map.");
  var rootNode=new MapTask{Title="Root",Prompt="Write-Output root",Acceptance="Root complete"};var dependent=new MapTask{Title="Dependent",Prompt="Write-Output dependent",Acceptance="Dependent complete"};var sibling=new MapTask{Title="Sibling",Prompt="Write-Output sibling",Acceptance="Optional"};
  var dagId=r.SaveDraftMap(new TaskMap{Title="DAG",Tasks=[rootNode,dependent,sibling],Edges=[new MapEdge(rootNode.Id,dependent.Id,MapEdgeKind.Dependency)]});var dag=State(r).Maps.Single(x=>x.Id==dagId);
  var roots=await r.StartSelectedMapTasksAsync(dagId,[rootNode.Id,dependent.Id],dag.Revision);Check(roots.Count==1&&dag.Tasks.Single(x=>x.Id==dependent.Id).Selected,"DAG did not launch only ready root and retain dependent authorization.");
  await r.WaitForIdleAsync();Check(dag.Tasks.Single(x=>x.Id==dependent.Id).WorkId!=null&&dag.Tasks.Single(x=>x.Id==sibling.Id).WorkId==null,"Dependent did not start exactly once or deselected sibling started.");
  var pendingRoot=new MapTask{Title="Unready",Prompt="Write-Output unready",Acceptance="Done"};var pending=new MapTask{Title="Pending",Prompt="Write-Output pending",Acceptance="Done"};var later=new MapTask{Title="Later",Prompt="Write-Output later",Acceptance="Done"};
  var activeId=r.SaveDraftMap(new TaskMap{Title="Active authorization",Tasks=[pendingRoot,pending,later],Edges=[new MapEdge(pendingRoot.Id,pending.Id,MapEdgeKind.Dependency)]});var active=State(r).Maps.Single(x=>x.Id==activeId);active.Status=MapStatus.Active;var authorizedRoot=new WorkUnit{Task="authorized root",Status=WorkStatus.Running};State(r).Work.Add(authorizedRoot);var rootTask=active.Tasks.Single(x=>x.Id==pendingRoot.Id);rootTask.WorkId=authorizedRoot.Id;rootTask.WorkIds=[authorizedRoot.Id];rootTask.Selected=true;active.Tasks.Single(x=>x.Id==pending.Id).Selected=true;
  await r.StartSelectedMapTasksAsync(activeId,[later.Id],active.Revision);Check(active.Tasks.Single(x=>x.Id==pending.Id).Selected,"Later active start revoked earlier pending authorization.");await r.WaitForIdleAsync();  var root=new WorkUnit{Task="original",Status=WorkStatus.Completed,Relationship=WorkRelationship.Original};State(r).Work.Add(root);
  var node=new MapTask{Title="Original",Prompt="Write-Output original",Acceptance="Done",Selected=true,WorkId=root.Id,WorkIds=[root.Id],Status=MapTaskStatus.Completed};var mapId=r.SaveDraftMap(new TaskMap{Title="Lineage",Tasks=[new MapTask{Title="Original",Prompt="Write-Output original",Acceptance="Done",Selected=true}]});
  var lineage=State(r).Maps.Single(x=>x.Id==mapId);lineage.Tasks[0].WorkId=root.Id;lineage.Tasks[0].WorkIds=[root.Id];lineage.Status=MapStatus.Active;Refresh(r);
  var child=await r.ReplyAfterCompletionAsync(root.Id,"Write-Output followup",Guid.NewGuid().ToString("N"));Check(State(r).Work.Single(x=>x.Id==child).Relationship==WorkRelationship.Followup&&lineage.Edges.Any(x=>x.Kind==MapEdgeKind.Followup),"Followup flattened into revision.");await r.WaitForIdleAsync();
  var debt=Store(r).Add(new TaskInteraction{WorkId=root.Id,Kind=InteractionKind.Obligation,Status=InteractionStatus.Pending,Required=true,Text="required"});
  var proposal=Store(r).Add(new TaskInteraction{WorkId=root.Id,Kind=InteractionKind.Followup,Status=InteractionStatus.Pending,Required=true,Text="followup",RelatedId=debt.Id});Refresh(r);
  Check(lineage.RequiredDebtCount==2&&lineage.Status==MapStatus.Active,"Required debt did not hold map open.");r.RejectFollowup(root.Id,proposal.Id,"Declined");Check(lineage.RequiredDebtCount==1,"Reject erased obligation.");r.ResolveObligation(root.Id,debt.Id,"Settled");Check(lineage.RequiredDebtCount==0,"Resolved debt persisted.");
  var sender=new WorkUnit{Status=WorkStatus.Running};var receiver=new WorkUnit{Status=WorkStatus.Running};State(r).Work.AddRange([sender,receiver]);var peer=r.AskPeer(sender.Id,receiver.Id,"Please acknowledge");Check(r.Inbox(receiver.Id,"turn-a").Any(x=>x.Id==peer.Id),"Peer inbox lost request.");r.AcknowledgePeer(receiver.Id,peer.Id,"Done");Check(r.Inbox(sender.Id,"turn-a").Any(x=>x.Id==peer.Id&&x.Status==InteractionStatus.Acknowledged),"Peer outcome invisible.");
  var steering=Store(r).Add(new TaskInteraction{WorkId=receiver.Id,Kind=InteractionKind.Steering,Status=InteractionStatus.Delivered,TurnId="turn-a",Text="read"});Check(!r.Inbox(receiver.Id,"turn-b").Any(x=>x.Id==steering.Id),"Steering leaked turns.");r.AcknowledgeMessage(receiver.Id,"turn-a",steering.Id);
 }
}
