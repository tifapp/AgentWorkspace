using AgentOS.Core;
namespace AgentOS.App;
internal static class PreviewData
{
 public static ProjectState Create(bool updated=false,bool empty=false)
 {
  var state=new ProjectState{ProjectPath=Path.Combine(Path.GetTempPath(),"AgentOS UI preview"),Generation=updated?2:1};
  if(empty)return state;
  WorkUnit Item(string id,string task,WorkStatus status,string detail,string? report=null,string? parent=null)=>new(){Id=id,Task=task,Title=task,Status=status,Detail=detail,CodexReport=report,ParentId=parent,CreatedAt=DateTimeOffset.Now.AddMinutes(-24),UpdatedAt=DateTimeOffset.Now};
  state.Work.Add(Item("preview-completed","Improve settings layout",WorkStatus.Completed,"Integrated after validation.",updated?"Settings layout updated. Focus and drafts should remain stable.\nValidation passed after the update.":"Settings now use native controls and fit narrow windows.\nValidation passed; the changed view remains readable."));
  state.Work.Add(Item("preview-child","Refine keyboard labels",WorkStatus.Completed,"Integrated child task.","Added accessible names and keyboard hints.","preview-completed"));
  state.Work.Add(Item("preview-running","Review task map spacing",updated?WorkStatus.Validating:WorkStatus.Running,updated?"Agent is checking the revised row spacing and focus order.":"Agent is inspecting row spacing and focus order."));
  state.Work.Add(Item("preview-waiting","Check validation output",WorkStatus.Waiting,"Waiting for the running check."));
  state.Work.Add(Item("preview-failed","Handle an unavailable log",WorkStatus.Failed,"Validation exited with code 1.","The log could not be opened; retry from the saved candidate."));
  state.Work.Add(Item("preview-private","Review private candidate",WorkStatus.Private,"Candidate is ready for review.","Updated the project summary without integrating it."));
  state.Work.Add(Item("preview-stale","Reconcile old candidate",WorkStatus.Stale,"Base commit changed; revision is needed."));
  state.Work.Add(Item("preview-response","Reconcile shared settings",WorkStatus.NeedsResponse,"Conflict response owed; candidate retained."));
  state.Conflicts.Add(new ConflictNotice{Id="preview-conflict",WorkId="preview-response",Cause="Shared path changed during publication",Paths=["src/settings.json"],BaseCommit="base123",CurrentCommit="current456",HolderWorkId="preview-completed",DeferredCandidateCommit="candidate789"});
  state.Work.Add(Item("preview-parked","Resolve deferred styles",WorkStatus.Parked,"Conflict response recorded; source remains unresolved."));
  state.Conflicts.Add(new ConflictNotice{Id="preview-parked-conflict",WorkId="preview-parked",Cause="Conflicting style edit",Paths=["src/styles.css"],BaseCommit="base123",CurrentCommit="current456",DeferredCandidateCommit="candidate888",Response="Coordinate the style changes."});
  state.Work.Add(Item("preview-unknown","Recover interrupted task",WorkStatus.Unknown,"The prior host result is unknown."));
  state.Work[0].ChangedPaths.Add("src/AgentOS.App/MainWindow.cs");state.Work[0].Diff="diff --git a/MainWindow.cs b/MainWindow.cs\n+Use a compact header and stable detail pane.";
  state.Work[0].Evidence.Add(new ValidationEvidence{Commit="abc123456789",Tree="def987654321",AgainstCommit="000111222333",Command=".\\scripts\\ValidateCompactUi.ps1",Environment="Windows PowerShell",ExitCode=0,SourceUnchanged=true});
  state.Decisions.Add(new HumanDecision{Id="preview-decision",WorkId="preview-private",Candidate="abc123456789",Destination="main",Scope="Local release",Explanation="Review the candidate and validation evidence.",Status=DecisionStatus.Pending});
  return state;
 }
}

