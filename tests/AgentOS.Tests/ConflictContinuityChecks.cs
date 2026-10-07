using AgentOS.Core;
namespace AgentOS.Tests;
internal static class ConflictContinuityChecks
{
 static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
 static WorkUnit Work(ProjectRuntime r,string id)=>r.Snapshot.Work.Single(x=>x.Id==id);
 static ConflictNotice Notice(ProjectRuntime r,string id)=>r.Snapshot.Conflicts.Last(x=>x.WorkId==id&&!x.Resolved&&!x.Abandoned);
 static Task<string> Ref(ProjectRuntime r)=>ReadRef(r);
 static async Task<string> ReadRef(ProjectRuntime r)=>(await Commands.Git(r.Snapshot.ProjectPath,"rev-parse",ProjectRuntime.IntegratedRef)).Checked();
 static async Task<string> Show(ProjectRuntime r,string path)=>(await Commands.Git(r.Snapshot.ProjectPath,"show",ProjectRuntime.IntegratedRef+":"+path)).Checked();
 static async Task<ProjectRuntime> Runtime(string root, string project)=>await ProjectRuntime.OpenInternal(project,Path.Combine(root,"state-"+Guid.NewGuid().ToString("N")),new ScriptHost(),Path.Combine(root,"coordination"));
 public static async Task RunAsync(Func<string,Func<Task>,Task> test,string root)
 {
  Task Test(string name,Func<Task> run)=>test("Conflict "+name,run);
  await Test("identical desired blob completes without notice",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'",false);var b=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);
   Check(Work(r,b).Status==WorkStatus.Completed&&r.Snapshot.Conflicts.All(x=>x.WorkId!=b),"Identical exact result was treated as conflict.");
   Check((await Show(r,"settings.json")).Contains("\"retries\":2"),"Desired result missing.");
  });
  await Test("identical executable mode completes without notice",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("git update-index --chmod=+x README.md",false);var b=await r.StartAsync("git update-index --chmod=+x README.md",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);
   var entry=(await Commands.Git(project,"ls-tree",ProjectRuntime.IntegratedRef,"--","README.md")).Checked();
   Check(entry.StartsWith("100755 ")&&Work(r,b).Status==WorkStatus.Completed&&r.Snapshot.Conflicts.All(x=>x.WorkId!=b),"Exact mode result was not recognized.");
  });
  await Test("same blob with different mode remains conflict",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content README.md exact; git add README.md; git update-index --chmod=+x README.md",false);
   var b=await r.StartAsync("Set-Content README.md exact",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);
   Check(Work(r,b).Status==WorkStatus.NeedsResponse&&Notice(r,b).Paths.Contains("README.md"),"Different mode with same blob bypassed conflict.");
  });
  await Test("live conflicting task can coordinate and wait until job ends",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'",false);
   var b=await r.StartAsync("Set-Content settings.json '{\"retries\":3}'; while(!(Test-Path .git/fixture-release)){Start-Sleep -Milliseconds 50}");
   using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
   while(Work(r,a).Status!=WorkStatus.Private || Work(r,b).Status!=WorkStatus.Running)await Task.Delay(25,timeout.Token);
   await r.IntegrateAsync(a);
   while(!File.ReadAllText(Path.Combine(Work(r,b).Workspace,"settings.json")).Contains("\"retries\":3"))await Task.Delay(25,timeout.Token);
   await r.AfterManagedTurnAsync(b,CancellationToken.None);
   Check(Work(r,b).Status==WorkStatus.NeedsResponse&&Notice(r,b).Response==null,"Fixture did not create a real conflict while host runs.");
   var c=await r.StartAsync("while(!(Test-Path .git/fixture-release)){Start-Sleep -Milliseconds 50}",false);var d=await r.StartAsync("while(!(Test-Path .git/fixture-release)){Start-Sleep -Milliseconds 50}",false);
   while(Work(r,c).Status!=WorkStatus.Running||Work(r,d).Status!=WorkStatus.Running)await Task.Delay(25,timeout.Token);
   var peer=r.AskPeer(b,c,"Can you coordinate?");r.AcknowledgePeer(c,peer.Id,"Yes");
   var handoff=r.AskPeer(c,b,"Please hand this on.");r.HandoffPeer(b,handoff.Id,d);r.AcknowledgePeer(d,handoff.Id,"Done");
   var wait=r.CreateWait(c,WaitKind.Task,b);Check(wait.Status==InteractionStatus.Pending,"Live conflicting task was treated as ended.");
   using var shortWait=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));try{await r.AwaitWaitAsync(c,wait.Id,shortWait.Token);}catch(OperationCanceledException){}
   Check(r.Inbox(c).Single(x=>x.Id==wait.Id).Status==InteractionStatus.Pending,"Task wait settled before job ended.");r.CancelWait(c,wait.Id);
   var cycleA=r.CreateWait(b,WaitKind.Task,d);try{r.CreateWait(d,WaitKind.Task,b);throw new Exception("Wait cycle accepted.");}catch(InvalidOperationException){}r.CancelWait(b,cycleA.Id);
   r.RespondToConflict(b,Notice(r,b).Id,"Continue while parked.");await r.AfterManagedTurnAsync(b,CancellationToken.None);
   Check(Work(r,b).Status==WorkStatus.Parked,"Live conflict did not park after response.");
   var message=r.SendPeerMessage(c,b,"Message to parked live task.");
   await ManagedCodexHost.DeliverPendingPeerMessagesAsync(r,b,_=>Task.FromException(new IOException("Transport failed")),_=>{});
   Check(r.PendingPeerMessages(b).Single().Id==message.Id,"Failed transport lost queued message.");
   var delivered=0;await ManagedCodexHost.DeliverPendingPeerMessagesAsync(r,b,_=>{delivered++;return Task.CompletedTask;},_=>{});
   await ManagedCodexHost.DeliverPendingPeerMessagesAsync(r,b,_=>{delivered++;return Task.CompletedTask;},_=>{});
   Check(r.Snapshot.PeerMessages.Single(x=>x.Id==message.Id).DeliveredAt!=null&&r.Snapshot.Events.Count(x=>x.Kind=="PeerDelivered"&&x.Message.Contains(message.Id))==1&&delivered==1,"Parked live delivery was lost or duplicated.");
   var terminal=r.SendPeerMessage(c,b,"Target ends during delivery.");
   await ManagedCodexHost.DeliverPendingPeerMessagesAsync(r,b,async _=>{
    foreach(var id in new[]{b,c,d})File.WriteAllText(Path.Combine(Work(r,id).Workspace,".git","fixture-release"),"go");
    await r.WaitForIdleAsync();
   },_=>{});
   Check(r.PendingPeerMessages(b).Single().Id==terminal.Id&&r.Snapshot.PeerMessages.Single(x=>x.Id==terminal.Id).DeliveredAt==null,"Target ending during transport falsely marked delivery.");
   try{r.CreateWait(b,WaitKind.Task,c);throw new Exception("Inactive parked task created a wait.");}catch(InvalidOperationException){}
   try{r.AskPeer(b,c,"late");throw new Exception("Inactive parked task asked peer.");}catch(InvalidOperationException){}
  });
  await Test("exact shared match does not bypass owed response",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'",false);var b=await r.StartAsync("Set-Content settings.json '{\"retries\":3}'",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);var owed=Notice(r,b);
   var c=await r.StartAsync("Set-Content settings.json '{\"retries\":3}'",false);await r.WaitForIdleAsync();await r.IntegrateAsync(c);var shared=await Ref(r);
   var prompt=await r.AfterManagedTurnAsync(b,CancellationToken.None);
   Check(prompt?.Contains(owed.Id)==true&&Work(r,b).Status==WorkStatus.NeedsResponse&&owed.Response==null&&await Ref(r)==shared,"Exact shared result bypassed existing response obligation.");
  });
  await Test("three agent resolved path changed after notice requires new response",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2,\"cancellation\":false}'",false);var b=await r.StartAsync("Set-Content settings.json '{\"retries\":1,\"cancellation\":true}'",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);var first=Notice(r,b);r.RespondToConflict(b,first.Id,"I will reconcile.");
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"settings.json"),"{\"retries\":2,\"cancellation\":true}");r.ResolveConflict(b,first.Id,"Both settings.");
   var c=await r.StartAsync("Set-Content settings.json '{\"retries\":3,\"cancellation\":false}'; Set-Content c-only.txt c",false);await r.WaitForIdleAsync();await r.IntegrateAsync(c);var afterC=await Ref(r);
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"b-only.txt"),"b");
   var state=(ProjectState)typeof(ProjectRuntime).GetField("_state",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(r)!;state.Work.Single(x=>x.Id==b).AutoIntegrate=true;
   await r.AfterManagedTurnAsync(b,CancellationToken.None);var second=Notice(r,b);
   Check(second.Id!=first.Id&&second.CurrentCommit==afterC&&second.Response==null,"New holder version did not create a new response obligation.");
   Check(first.Response!=null&&first.DeferredCandidateCommit!=null&&await Ref(r)==afterC,"Old response/candidate or C publication was lost.");
   Check((await Show(r,"settings.json")).Contains("\"retries\":3")&&(await Show(r,"c-only.txt")).Trim()=="c","C publication was overwritten.");
   r.RespondToConflict(b,second.Id,"Reconcile C setting too.");
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"settings.json"),"{\"retries\":3,\"cancellation\":true}");r.ResolveConflict(b,second.Id,"Keep C retries and local cancellation.");
   await r.AfterManagedTurnAsync(b,CancellationToken.None);
   Check(Work(r,b).Status==WorkStatus.Completed&&r.Snapshot.Conflicts.Where(x=>x.WorkId==b).All(x=>x.Resolved),"Superseded old notice prevented recovery.");
   Check((await Show(r,"settings.json")).Contains("\"retries\":3")&&(await Show(r,"settings.json")).Contains("\"cancellation\":true"),"Recovery dropped a setting.");
   Check((await Show(r,"b-only.txt")).Trim()=="b"&&(await Show(r,"c-only.txt")).Trim()=="c","Independent edits did not compose once.");
  });
  await Test("multi path notice supersedes only newer blocked path",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'; Set-Content README.md holder",false);
   var b=await r.StartAsync("Set-Content settings.json '{\"retries\":3}'; Set-Content README.md blocked",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);var old=Notice(r,b);
   Check(old.Paths.Count==2,"Fixture did not block two paths.");r.RespondToConflict(b,old.Id,"Reconcile both paths.");
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"settings.json"),"{\"retries\":2,\"local\":true}");
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"README.md"),"holder and blocked");r.ResolveConflict(b,old.Id,"Both source changes.");
   var c=await r.StartAsync("Set-Content settings.json '{\"retries\":4}'",false);await r.WaitForIdleAsync();await r.IntegrateAsync(c);
   var state=(ProjectState)typeof(ProjectRuntime).GetField("_state",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(r)!;state.Work.Single(x=>x.Id==b).AutoIntegrate=true;
   await r.AfterManagedTurnAsync(b,CancellationToken.None);var newer=Notice(r,b);
   Check(newer.Paths.SequenceEqual(["settings.json"])&&r.Snapshot.Conflicts.Single(x=>x.Id==old.Id).SupersededPaths.SequenceEqual(["settings.json"]),"Supersession consumed unrelated old path.");
   r.RespondToConflict(b,newer.Id,"Include latest retries.");File.WriteAllText(Path.Combine(Work(r,b).Workspace,"settings.json"),"{\"retries\":4,\"local\":true}");r.ResolveConflict(b,newer.Id,"Latest retries retained.");
   await r.AfterManagedTurnAsync(b,CancellationToken.None);
   Check(Work(r,b).Status==WorkStatus.Completed&&(await Show(r,"README.md")).Contains("holder and blocked")&&(await Show(r,"settings.json")).Contains("\"retries\":4"),"Partial supersession failed to publish both reconciled paths.");
  });  await Test("three agent update during validation retains candidate and re-notices",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'",false);
   var b=await r.StartAsync("Set-Content settings.json '{\"retries\":3}'",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);var old=Notice(r,b);r.RespondToConflict(b,old.Id,"Reconcile holder.");
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"settings.json"),"{\"retries\":2,\"local\":true}");r.ResolveConflict(b,old.Id,"Preserve both.");
   var c=await r.StartAsync("Set-Content settings.json '{\"retries\":4}'",false);await r.WaitForIdleAsync();
   var shared=await Ref(r);var tree=(await Commands.Git(project,"rev-parse",Work(r,c).CandidateCommit+"^{tree}")).Checked();
   var cCommit=(await Commands.Git(project,"-c","user.name=fixture","-c","user.email=fixture@example.invalid","commit-tree",tree,"-p",shared,"-m","C validation race")).Checked();
   var state=(ProjectState)typeof(ProjectRuntime).GetField("_state",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(r)!;
   var wb=state.Work.Single(x=>x.Id==b);wb.AutoIntegrate=true;
   wb.ValidationCommand="& git -C '"+project.Replace("'","''")+"' update-ref "+ProjectRuntime.IntegratedRef+" "+cCommit+" "+shared+"; if($LASTEXITCODE -ne 0){exit 7}; Write-Output passed";
   await r.AfterManagedTurnAsync(b,CancellationToken.None);var newer=Notice(r,b);
   Check(newer.Id!=old.Id&&newer.Cause=="UpdateRefRace"&&await Ref(r)==cCommit&&newer.Response==null,"Validation race silently published B or missed re-notice.");
   Check((await Commands.Git(project,"rev-parse","refs/agent-os/deferred/"+old.Id)).Checked()==old.DeferredCandidateCommit,"Validation race lost original candidate ref.");
  });  await Test("equivalent human escalation is durable and deduplicated",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Set-Content settings.json '{\"retries\":2}'",false);var b=await r.StartAsync("Set-Content settings.json '{\"retries\":3}'",false);await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);var n=Notice(r,b);
   r.RespondToConflict(b,n.Id,"Unable to reconcile.");var first=r.EscalateConflict(b,n.Id,"Human decision needed.");var duplicate=r.EscalateConflict(b,n.Id,"Human decision needed.");
   Check(first.Id==duplicate.Id&&r.Snapshot.Escalations.Count==1,"Equivalent escalation duplicated.");
   var parallel=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>r.EscalateConflict(b,n.Id,"Human decision needed."))));
   Check(parallel.All(x=>x.Id==first.Id)&&r.Snapshot.Escalations.Count==1,"Concurrent equivalent escalations duplicated.");
   File.WriteAllText(Path.Combine(Work(r,b).Workspace,"independent.txt"),"progress");
   var state=(ProjectState)typeof(ProjectRuntime).GetField("_state",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(r)!;state.Work.Single(x=>x.Id==b).AutoIntegrate=true;
   await r.AfterManagedTurnAsync(b,CancellationToken.None);
   Check((await Show(r,"independent.txt")).Trim()=="progress"&&r.Snapshot.Escalations.Count==1,"Escalation blocked independent progress or duplicated contact.");
  });
  await Test("identical deletion completes without notice",async()=>{
   var project=await PracticeProject.CreateAsync(root);await using var r=await Runtime(root,project);r.Configure("Write-Output passed");
   var a=await r.StartAsync("Remove-Item README.md",false);var b=await r.StartAsync("Remove-Item README.md",false);
   await r.WaitForIdleAsync();await r.IntegrateAsync(a);await r.IntegrateAsync(b);
   Check(Work(r,b).Status==WorkStatus.Completed&&r.Snapshot.Conflicts.All(x=>x.WorkId!=b),"Identical deletion caused a conflict.");
   Check((await Commands.Git(project,"ls-tree",ProjectRuntime.IntegratedRef,"--","README.md")).Checked().Length==0,"Deletion was not published.");
  });
  await Test("queued peer message and request retain delivery boundaries",async()=>{
   var project=await PracticeProject.CreateAsync(root);var state=Path.Combine(root,"state-"+Guid.NewGuid().ToString("N"));
   string queued, target, sender, request;
   await using(var r=await ProjectRuntime.OpenInternal(project,state,new ScriptHost(),Path.Combine(root,"coordination")))
   {
    r.Configure("Write-Output passed");sender=await r.StartAsync("Start-Sleep -Seconds 3",false);target=await r.StartAsync("Start-Sleep -Seconds 3",false);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(Work(r,sender).Status!=WorkStatus.Running||Work(r,target).Status!=WorkStatus.Running)await Task.Delay(25,timeout.Token);
    queued=r.SendPeerMessage(sender,target,"Preserve path.").Id;request=r.AskPeer(sender,target,"Who owns path?").Id;
    await r.WaitForIdleAsync();Check(r.PendingPeerMessages(target).Single().Id==queued,"Undelivered message disappeared when target ended.");
   }
   await using(var r=await ProjectRuntime.OpenInternal(project,state,new ScriptHost(),Path.Combine(root,"coordination")))
   {
    Check(r.PendingPeerMessages(target).Single().Id==queued,"Restart lost queued message.");
    await ManagedCodexHost.DeliverPendingPeerMessagesAsync(r,target,_=>Task.CompletedTask,_=>{});
    Check(r.PendingPeerMessages(target).Single().Id==queued&&r.Snapshot.PeerMessages.Single(x=>x.Id==queued).DeliveredAt==null,"Failed delivery falsely marked queued message delivered.");
    Check(r.Inbox(target).Single(x=>x.Id==request).Status!=InteractionStatus.Acknowledged,"Delivery falsely acknowledged request.");
   }
  });
  await Test("managed host loop suspends bounded nonresponse and resumes same thread",async()=>{
   var project=await PracticeProject.CreateAsync(root);var state=Path.Combine(root,"state-"+Guid.NewGuid().ToString("N"));string blocked, noticeId, candidate;
   await using(var r=await ProjectRuntime.OpenInternal(project,state,new RevisingHost(),Path.Combine(root,"coordination")))
   {
    r.Configure("Write-Output passed");await r.StartAsync("retry");blocked=await r.StartAsync("cancel");await r.WaitForIdleAsync();
    var n=Notice(r,blocked);noticeId=n.Id;candidate=n.DeferredCandidateCommit!;r.RecordHostThread(blocked,"thread-original");var turns=0;
    var result=await ManagedCodexHost.RunContinuationLoopAsync(Work(r,blocked),r,"thread-original",ProjectRuntime.ConflictPrompt(n),input=>{turns++;Check(input.Contains(noticeId),"Continuation lost notice.");return Task.FromResult(true);},_=>{},()=>null,()=>null,CancellationToken.None);
    Check(result.ThreadId=="thread-original"&&turns==2&&Work(r,blocked).Status==WorkStatus.NeedsResponse&&Notice(r,blocked).Response==null,"Host loop did not suspend with response owed.");
   }
   await using(var r=await ProjectRuntime.OpenInternal(project,state,new ScriptHost(),Path.Combine(root,"coordination")))
   {
    var n=Notice(r,blocked);Check(n.Id==noticeId&&n.DeferredCandidateCommit==candidate&&n.Response==null&&Work(r,blocked).ThreadId=="thread-original","Restart lost obligation, candidate, or thread.");
    var turns=0;var result=await ManagedCodexHost.RunContinuationLoopAsync(Work(r,blocked),r,"thread-original",ProjectRuntime.ContinuationPrompt(n),input=>{turns++;return Task.FromResult(false);},_=>{},()=>null,()=>null,CancellationToken.None);
    Check(!result.TurnCompleted&&turns==1&&Work(r,blocked).Status!=WorkStatus.Completed&&Notice(r,blocked).Response==null,"Failed host turn completed conflicted work.");
    using(var canceled=new CancellationTokenSource()){canceled.Cancel();try{await ManagedCodexHost.RunContinuationLoopAsync(Work(r,blocked),r,"thread-original",ProjectRuntime.ContinuationPrompt(n),_=>Task.FromResult(true),_=>{},()=>null,()=>null,canceled.Token);throw new Exception("Canceled host continued.");}catch(OperationCanceledException){}}
    Check(Notice(r,blocked).Response==null&&Notice(r,blocked).DeferredCandidateCommit==candidate,"Cancellation lost response or candidate.");
    r.RespondToConflict(blocked,noticeId,"Keep both settings.");
    var resumed=await ManagedCodexHost.RunContinuationLoopAsync(Work(r,blocked),r,"thread-original",ProjectRuntime.ContinuationPrompt(Notice(r,blocked)),input=>{File.WriteAllText(Path.Combine(Work(r,blocked).Workspace,"settings.json"),"{\"retries\":2,\"cancellation\":true}");r.ResolveConflict(blocked,noticeId,"Combined settings.");return Task.FromResult(true);},_=>{},()=>null,()=>null,CancellationToken.None);
    Check(resumed.TurnCompleted&&resumed.ThreadId=="thread-original"&&Work(r,blocked).Status==WorkStatus.Completed,"Resumed same thread did not complete reconciled work.");
   }
  });
 }
}

