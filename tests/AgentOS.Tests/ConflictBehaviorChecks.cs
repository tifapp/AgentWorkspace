using AgentOS.Core;
namespace AgentOS.Tests;
internal static class ConflictBehaviorChecks
{
 const string Retry="Set-Content settings.json '{\"retries\":2,\"cancellation\":false}'";
 const string Cancel="Set-Content settings.json '{\"retries\":1,\"cancellation\":true}'";
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 static WorkUnit Work(ProjectRuntime r,string id)=>r.Snapshot.Work.Single(x=>x.Id==id);
 static ConflictNotice Notice(ProjectRuntime r,string id)=>r.Snapshot.Conflicts.Single(x=>x.WorkId==id);
 static async Task<string> Ref(ProjectRuntime r)=>(await Commands.Git(r.Snapshot.ProjectPath,"rev-parse",ProjectRuntime.IntegratedRef)).Checked();
 static async Task<string> Show(ProjectRuntime r,string path)=>(await Commands.Git(r.Snapshot.ProjectPath,"show",ProjectRuntime.IntegratedRef+":"+path)).Checked();
 static void Denied(Action action,string why){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception(why);}
 static async Task<(ProjectRuntime Runtime,string Holder,string Blocked,string Project,string StateRoot)> Conflict(string root,string? validation=null)
 {
  var project=await PracticeProject.CreateAsync(root);var state=Path.Combine(root,"state-"+Guid.NewGuid().ToString("N"));
  var r=await ProjectRuntime.OpenInternal(project,state,new RevisingHost(),Path.Combine(root,"coordination"));r.Configure(validation??"Write-Output passed");
  var a=await r.StartAsync("retry");var b=await r.StartAsync("cancel");await r.WaitForIdleAsync();
  Check(Work(r,a).Status==WorkStatus.Completed&&Work(r,b).Status==WorkStatus.NeedsResponse,"Fixture did not block the second candidate.");
  return(r,a,b,project,state);
 }
 public static async Task RunAsync(Func<string,Func<Task>,Task> test,string root)
 {
  Task Test(string name,Func<Task> scenario)=>test("Conflict "+name,scenario);
  await Test("Notice identities, deferred ref, and unchanged shared tree",async()=>{
   var f=await Conflict(root);await using var r=f.Runtime;var n=Notice(r,f.Blocked);var w=Work(r,f.Blocked);
   Check(n.Cause=="TouchedPathChanged"&&n.Paths.SequenceEqual(["settings.json"])&&n.PublicationBlocked,"Cause, path, or block flag is wrong.");
   Check(n.BaseCommit==w.BaseCommit&&n.CurrentCommit==Work(r,f.Holder).IntegratedCommit&&n.HolderWorkId==f.Holder&&n.DeferredCandidateCommit==w.CandidateCommit,"Notice lost exact work or commit identity.");
   Check((await Commands.Git(f.Project,"rev-parse","refs/agent-os/deferred/"+n.Id)).Checked()==w.CandidateCommit,"Deferred ref does not name original candidate.");
   Check(await Ref(r)==n.CurrentCommit&&(await Show(r,"settings.json")).Contains("\"cancellation\":false"),"Blocked content changed shared state.");
   Check(r.Snapshot.PeerMessages.Count==0&&r.Snapshot.InterruptRequests.Count==0&&r.Snapshot.Escalations.Count==0,"Conflict automatically coordinated.");
  });
  await Test("Exact one-time response and final-turn obligation",async()=>{
   var f=await Conflict(root);await using var r=f.Runtime;var n=Notice(r,f.Blocked);
   Denied(()=>r.RespondToConflict(f.Blocked,n.Id," \t"),"Blank response accepted.");
   Denied(()=>r.RespondToConflict(f.Holder,n.Id,"wrong work"),"Wrong work answered notice.");
   Denied(()=>r.RespondToConflict(f.Blocked,Guid.NewGuid().ToString("N"),"wrong notice"),"Wrong notice answered.");
   var prompt=await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None);
   Check(prompt?.Contains(n.Id)==true&&Work(r,f.Blocked).Status==WorkStatus.NeedsResponse&&Notice(r,f.Blocked).Response==null,"Final turn discharged owed response.");
   r.RespondToConflict(f.Blocked,n.Id,"  I will reconcile source.  ");
   Check(Notice(r,f.Blocked).Response=="I will reconcile source.","Free-form response was not retained.");
   Denied(()=>r.RespondToConflict(f.Blocked,n.Id,"second"),"Double response accepted.");
   Check(await Ref(r)==n.CurrentCommit&&Work(r,f.Blocked).Status!=WorkStatus.Completed,"Response alone published blocked work.");
  });
  await Test("Independent path publishes once while blocked path remains private",async()=>{
   var f=await Conflict(root);await using var r=f.Runtime;var n=Notice(r,f.Blocked);
   r.RespondToConflict(f.Blocked,n.Id,"Publish independent documentation; retain settings.");
   File.AppendAllText(Path.Combine(Work(r,f.Blocked).Workspace,"README.md"),"\nIndependent continuation\n");
   await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None);var published=await Ref(r);var content=await Show(r,"README.md");
   Check(Work(r,f.Blocked).Status==WorkStatus.Parked&&content.Contains("Independent continuation"),"Independent content did not publish with work parked.");
   Check(!(await Show(r,"settings.json")).Contains("\"cancellation\":true")&&!Notice(r,f.Blocked).Resolved,"Blocked content leaked.");
   await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None);
   Check(await Ref(r)==published&&(await Show(r,"README.md"))==content,"Repeated continuation published twice.");
   Check((await Commands.Git(f.Project,"rev-parse","refs/agent-os/deferred/"+n.Id)).Checked()==n.DeferredCandidateCommit,"Deferred ref changed.");
  });
  await Test("Unchanged resolution rejected, then revised source publishes",async()=>{
   var f=await Conflict(root);await using var r=f.Runtime;var n=Notice(r,f.Blocked);
   Denied(()=>r.ResolveConflict(f.Blocked,n.Id,"premature"),"Resolution skipped response.");
   r.RespondToConflict(f.Blocked,n.Id,"Include both settings.");r.ResolveConflict(f.Blocked,n.Id,"Claim source revised.");
   await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None);
   Check(Work(r,f.Blocked).Status==WorkStatus.Parked&&!Notice(r,f.Blocked).ResolutionRequested&&!Notice(r,f.Blocked).Resolved&&await Ref(r)==n.CurrentCommit,"Unchanged blocked source accepted.");
   File.WriteAllText(Path.Combine(Work(r,f.Blocked).Workspace,"settings.json"),"{\"retries\":2,\"cancellation\":true}");
   r.ResolveConflict(f.Blocked,n.Id,"Included peer retries and local cancellation.");await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None);
   Check(Notice(r,f.Blocked).Resolved&&Work(r,f.Blocked).Status==WorkStatus.Completed,"Revised source did not resolve conflict.");
   var settings=await Show(r,"settings.json");Check(settings.Contains("\"retries\":2")&&settings.Contains("\"cancellation\":true"),"Resolution lost either setting.");
  });
  await Test("Abandon requires response and retains original candidate",async()=>{
   var f=await Conflict(root);await using var r=f.Runtime;var n=Notice(r,f.Blocked);
   Denied(()=>r.AbandonConflict(f.Blocked,n.Id,"give up"),"Abandonment skipped response.");
   r.RespondToConflict(f.Blocked,n.Id,"I choose to abandon.");r.AbandonConflict(f.Blocked,n.Id,"Do not publish setting.");
   Check(Notice(r,f.Blocked).Abandoned&&Work(r,f.Blocked).Status==WorkStatus.Abandoned&&await Ref(r)==n.CurrentCommit,"Abandonment published or remained active.");
   Check((await Commands.Git(f.Project,"rev-parse","refs/agent-os/deferred/"+n.Id)).Checked()==n.DeferredCandidateCommit,"Abandonment deleted candidate ref.");
   Denied(()=>r.RespondToConflict(f.Blocked,n.Id,"late"),"Abandoned notice accepted response.");
  });
  await Test("Restart preserves obligation and original Git candidate",async()=>{
   var f=await Conflict(root);var n=Notice(f.Runtime,f.Blocked);await f.Runtime.DisposeAsync();
   await using var r=await ProjectRuntime.OpenInternal(f.Project,f.StateRoot,new ScriptHost(),Path.Combine(root,"coordination"));
   Check(Notice(r,f.Blocked).Response==null&&Work(r,f.Blocked).Status==WorkStatus.NeedsResponse,"Restart lost response obligation.");
   Check(Notice(r,f.Blocked).DeferredCandidateCommit==n.DeferredCandidateCommit&&(await Commands.Git(f.Project,"rev-parse","refs/agent-os/deferred/"+n.Id)).Checked()==n.DeferredCandidateCommit,"Restart lost candidate identity or ref.");
   Check((await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None))?.Contains(n.Id)==true,"Restart allowed final turn without response.");
  });
  await Test("Escalation requires response and exact conflict target",async()=>{
   var f=await Conflict(root);await using var r=f.Runtime;var n=Notice(r,f.Blocked);
   Denied(()=>r.EscalateConflict(f.Blocked,n.Id,"Need human decision."),"Escalation bypassed response.");
   Check(r.Snapshot.Escalations.Count==0,"Rejected escalation recorded.");
   r.RespondToConflict(f.Blocked,n.Id,"Ownership cannot be reconciled in this task.");
   Denied(()=>r.EscalateConflict(f.Holder,n.Id,"wrong work"),"Wrong work escalated.");
   Denied(()=>r.EscalateConflict(f.Blocked,Guid.NewGuid().ToString("N"),"wrong notice"),"Unknown notice escalated.");
   var escalation=r.EscalateConflict(f.Blocked,n.Id,"Need human decision.");
   Check(r.Snapshot.Escalations.Single().Id==escalation.Id&&escalation.WorkId==f.Blocked&&escalation.ConflictId==n.Id&&Work(r,f.Blocked).Status!=WorkStatus.Completed,"Explicit escalation targeted wrong notice or completed work.");
  });
  await Test("Peer delivery and interrupt require explicit exact targets",async()=>{
   var project=await PracticeProject.CreateAsync(root);
   await using var r=await ProjectRuntime.OpenInternal(project,Path.Combine(root,"state-"+Guid.NewGuid().ToString("N")),new ScriptHost(),Path.Combine(root,"coordination"));
   r.Configure("Write-Output passed");
   var a=await r.StartAsync("Start-Sleep -Seconds 8",false);var b=await r.StartAsync("Start-Sleep -Seconds 8",false);
   using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
   while(Work(r,a).Status!=WorkStatus.Running||Work(r,b).Status!=WorkStatus.Running)await Task.Delay(50,timeout.Token);
   Check(r.Snapshot.PeerMessages.Count==0&&r.Snapshot.InterruptRequests.Count==0,"Coordination was automatic.");
   var unknown=Guid.NewGuid().ToString("N");
   Denied(()=>r.SendPeerMessage(a,unknown,"wrong target"),"Unknown peer accepted message.");
   Denied(()=>r.InterruptManagedTask(a,unknown,"wrong target"),"Unknown peer accepted interrupt.");
   Check(r.Snapshot.PeerMessages.Count==0&&r.Snapshot.InterruptRequests.Count==0,"Rejected coordination recorded an action.");
   var message=r.SendPeerMessage(a,b,"Preserve the shared setting.");
   Check(r.PendingPeerMessages(b).Single().Id==message.Id&&r.PendingPeerMessages(a).Count==0,"Peer message was delivered to wrong inbox.");
   r.MarkPeerDelivered(b,message.Id);
   Check(r.PendingPeerMessages(b).Count==0&&r.Snapshot.PeerMessages.Single().DeliveredAt!=null,"Exact delivery was not retained.");
   var interrupt=r.InterruptManagedTask(a,b,"Stop this exact task.");
   Check(r.Snapshot.InterruptRequests.Single().Id==interrupt.Id&&interrupt.TargetWorkId==b&&r.Snapshot.InterruptRequests.Single().FromWorkId==a,"Interrupt did not retain exact requester and target.");
   await r.WaitForIdleAsync();
  });
  await Test("Two blocked paths stay private under one exact notice",async()=>{
   var project=await PracticeProject.CreateAsync(root);
   await using var r=await ProjectRuntime.OpenInternal(project,Path.Combine(root,"state-"+Guid.NewGuid().ToString("N")),new ScriptHost(),Path.Combine(root,"coordination"));
   r.Configure("Write-Output passed");
   var a=await r.StartAsync(Retry+"; Add-Content README.md 'holder line'",false);
   var b=await r.StartAsync(Cancel+"; Add-Content README.md 'blocked line'",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);var shared=await Ref(r);await r.IntegrateAsync(b);
   var n=Notice(r,b);
   Check(n.Cause=="TouchedPathChanged"&&n.Paths.OrderBy(x=>x).SequenceEqual(new[]{"README.md","settings.json"}),"Both changed paths were not blocked in the same notice.");
   Check(n.HolderWorkId==a&&n.CurrentCommit==shared&&n.DeferredCandidateCommit==Work(r,b).CandidateCommit,"Multi-path notice lost holder or candidate identity.");
   Check(await Ref(r)==shared&&(await Show(r,"README.md")).Contains("holder line")&&!(await Show(r,"README.md")).Contains("blocked line"),"Multi-path blocked content leaked into shared tree.");
  });
  await Test("Validation failure retains candidate and shared ref",async()=>{
   var f=await Conflict(root,"if ((Get-Content README.md -Raw) -match 'invalid continuation') { exit 7 }; Write-Output passed");await using var r=f.Runtime;var n=Notice(r,f.Blocked);
   r.RespondToConflict(f.Blocked,n.Id,"Publish separate documentation.");
   File.AppendAllText(Path.Combine(Work(r,f.Blocked).Workspace,"README.md"),"\ninvalid continuation\n");
   await r.AfterManagedTurnAsync(f.Blocked,CancellationToken.None);
   Check(Work(r,f.Blocked).Status==WorkStatus.Failed&&await Ref(r)==n.CurrentCommit&&!Notice(r,f.Blocked).Resolved,"Failed validation published source or resolved conflict.");
   Check((await Commands.Git(f.Project,"rev-parse","refs/agent-os/deferred/"+n.Id)).Checked()==n.DeferredCandidateCommit,"Failed validation lost original candidate.");
  });
 }
}
