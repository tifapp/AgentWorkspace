using System.Security.Cryptography;
namespace AgentOS.Core;
public enum MapStatus { Draft, Active, Completed, Canceled }
public enum MapEdgeKind { Dependency, Related, Followup }
public enum MapTaskStatus { Draft, Ready, Running, Completed, Failed, Canceled, Private, Waiting, Stale, Unknown, Validating, NeedsResponse, Parked, Abandoned }
public sealed class TaskMap
{
 public string Id { get; set; } = Guid.NewGuid().ToString("N");
 public string Title { get; set; } = "Untitled task map";
 public string ProjectPath { get; set; } = "";
 public MapStatus Status { get; set; } = MapStatus.Draft;
 public long Revision { get; set; }
 public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
 public List<MapTask> Tasks { get; set; } = [];
 public List<MapEdge> Edges { get; set; } = [];
 public List<ContextCitation> Citations { get; set; } = [];
 public List<ContextCitation> CitationHistory { get; set; } = [];
 public List<ContextArtifactRef> ContextRefs { get; set; } = [];
 public List<ContextArtifactRef> ContextRefHistory { get; set; } = [];
 public int RequiredDebtCount { get; set; }
}
public sealed class MapTask
{
 public string Id { get; set; } = Guid.NewGuid().ToString("N");
 public string Title { get; set; } = "";
 public string Prompt { get; set; } = "";
 public string Acceptance { get; set; } = "";
 public MapTaskStatus Status { get; set; } = MapTaskStatus.Draft;
 public string? WorkId { get; set; }
 public List<string> WorkIds { get; set; } = [];
 public bool Selected { get; set; }
 public int RequiredDebtCount { get; set; }
}
public sealed record MapEdge(string FromTaskId, string ToTaskId, MapEdgeKind Kind);
public sealed record ContextCitation(string Id, string Source, string Sha256, DateTimeOffset CapturedAt)
{
 public static ContextCitation Create(string source, ReadOnlySpan<byte> content) => new(Guid.NewGuid().ToString("N"), source, Convert.ToHexString(SHA256.HashData(content)), DateTimeOffset.UtcNow);
}
public static class TaskMapRules
{
 public static void Validate(TaskMap map)
 {
  if (!Guid.TryParseExact(map.Id, "N", out _) || string.IsNullOrWhiteSpace(map.Title)) throw new ArgumentException("A map needs an identity and title.");
  if (map.Tasks == null || map.Edges == null || map.Citations == null || map.CitationHistory == null || map.ContextRefs == null || map.ContextRefHistory == null || map.Tasks.Count < 1) throw new ArgumentException("A map needs tasks and valid collections.");
  if (map.Tasks.Any(t => t == null || !Guid.TryParseExact(t.Id, "N", out _) || string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Prompt) || string.IsNullOrWhiteSpace(t.Acceptance))) throw new ArgumentException("Every task needs an identity, title, prompt and acceptance criteria.");
  var ids = map.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
  if (ids.Count != map.Tasks.Count || map.Edges.Any(e => e == null || !Enum.IsDefined(e.Kind) || !ids.Contains(e.FromTaskId) || !ids.Contains(e.ToTaskId) || e.FromTaskId == e.ToTaskId) || map.Edges.Distinct().Count() != map.Edges.Count) throw new ArgumentException("Map edges must uniquely join distinct tasks in this map.");
  var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
  void Visit(string id)
  {
   if (visited.Contains(id)) return;
   if (!visiting.Add(id)) throw new ArgumentException("Task dependency cycle.");
   foreach (var edge in map.Edges.Where(e => e.Kind == MapEdgeKind.Dependency && e.ToTaskId == id)) Visit(edge.FromTaskId);
   visiting.Remove(id); visited.Add(id);
  }
  foreach (var id in ids) Visit(id);
  if (map.Citations.Any(c => c == null || !Guid.TryParseExact(c.Id, "N", out _) || c.Sha256 == null || c.Sha256.Length != 64 || !c.Sha256.All(Uri.IsHexDigit))) throw new ArgumentException("Captured citations need valid immutable hashes.");
  if (map.Citations.Select(c => c.Id).Distinct().Count() != map.Citations.Count) throw new ArgumentException("Duplicate citation identity.");
 }
 public static bool DependenciesComplete(TaskMap map, MapTask task) => map.Edges.Where(e => e.Kind == MapEdgeKind.Dependency && e.ToTaskId == task.Id).All(e => map.Tasks.Single(t => t.Id == e.FromTaskId).Status == MapTaskStatus.Completed);
}
public sealed partial class ProjectRuntime
{
 public string SaveDraftMap(TaskMap input, long? expectedRevision = null)
 {
  ObjectDisposedException.ThrowIf(_disposed, this); TaskMapRules.Validate(input);
  string id;
  lock (_sync)
  {
   var existing = _state.Maps.SingleOrDefault(m => m.Id == input.Id);
   if (existing != null && (existing.Status != MapStatus.Draft || existing.Revision != expectedRevision)) throw new InvalidOperationException("The draft changed or is active. Reload it before editing.");
   if (existing == null && expectedRevision != null) throw new InvalidOperationException("The draft no longer exists.");
   var map = JsonFormat.Copy(input); map.ProjectPath = _state.ProjectPath; map.Status = MapStatus.Draft; map.RequiredDebtCount = 0;
   foreach (var task in map.Tasks) { task.WorkId = null; task.WorkIds = []; task.Status = MapTaskStatus.Draft; task.RequiredDebtCount = 0; }
   new ContextArtifacts(_store.Root).Verify(map.ContextRefs);
   map.Revision = (existing?.Revision ?? 0) + 1;
   if (existing != null)
   {
    foreach(var old in existing.Citations.Concat(existing.CitationHistory)) if(map.Citations.Any(c=>c.Id==old.Id&&c!=old))throw new InvalidOperationException("Captured citation identity cannot be replaced.");
    map.CitationHistory=existing.CitationHistory.Concat(existing.Citations).Concat(map.Citations).DistinctBy(c=>c.Id).ToList();
    foreach(var old in existing.ContextRefs.Concat(existing.ContextRefHistory)) if(map.ContextRefs.Any(c=>c.Id==old.Id&&c!=old))throw new InvalidOperationException("Accepted context identity cannot be replaced.");
    map.ContextRefHistory=existing.ContextRefHistory.Concat(existing.ContextRefs).Concat(map.ContextRefs).DistinctBy(c=>c.Id).ToList();if(map.CitationHistory.Count>256||map.ContextRefHistory.Count>256)throw new InvalidOperationException("Citation identity history is full.");
    _state.Maps[_state.Maps.IndexOf(existing)] = map;
   }
   else { map.CitationHistory=map.Citations.ToList();map.ContextRefHistory=map.ContextRefs.ToList();_state.Maps.Add(map); }
   id = map.Id; Event(null, "MapDraft", $"Saved map {id} revision {map.Revision}; no task was started."); Save();
  }
  Changed?.Invoke(); return id;
 }
 public void CancelPendingMapForUpdate(string mapId) { lock(_sync) { if(_updateDrain==null)throw new InvalidOperationException("Project is not draining for an update."); var map=_state.Maps.Single(m=>m.Id==mapId); if(map.Status!=MapStatus.Active)throw new InvalidOperationException("Only an active map can be canceled."); map.Status=MapStatus.Canceled;map.Revision++;foreach(var task in map.Tasks.Where(t=>t.WorkId==null))task.Status=MapTaskStatus.Canceled;Event(null,"UpdateMapCanceled","User canceled unlaunched map tasks for signed update.");Save(); } Changed?.Invoke(); }
 public Task<IReadOnlyList<string>> StartSelectedMapTasksAsync(string mapId)
 {
  ObjectDisposedException.ThrowIf(_disposed, this);
  List<string> launched;
  lock (_sync)
  {
   RequireUpdateAdmission(); var map = _state.Maps.Single(m => m.Id == mapId); TaskMapRules.Validate(map);
   if (map.Status is MapStatus.Canceled or MapStatus.Completed) throw new InvalidOperationException("The map is closed.");
   if (string.IsNullOrWhiteSpace(_state.ValidationCommand)) throw new InvalidOperationException("Save a validation command in Project setup first.");
   UpdateMapStatuses();
   if (!map.Tasks.Any(t => t.Selected && t.WorkId == null && TaskMapRules.DependenciesComplete(map, t)))
    throw new InvalidOperationException("Select a ready task; dependencies must finish first.");
   map.Status = MapStatus.Active;
   launched = LaunchReadyMapTasks(map);
   Save();
  }
  Changed?.Invoke(); return Task.FromResult<IReadOnlyList<string>>(launched);
 }
 public Task<IReadOnlyList<string>> StartSelectedMapTasksAsync(string mapId,IReadOnlyList<string> selectedTaskIds,long expectedRevision)
 {
  ObjectDisposedException.ThrowIf(_disposed,this);
  if(selectedTaskIds==null||selectedTaskIds.Count==0||selectedTaskIds.Any(id=>!Guid.TryParseExact(id,"N",out _))||selectedTaskIds.Distinct(StringComparer.Ordinal).Count()!=selectedTaskIds.Count)throw new ArgumentException("Select distinct existing map task IDs.");
  List<string> launched;
  lock(_sync)
  {
   RequireUpdateAdmission();var map=_state.Maps.Single(m=>m.Id==mapId);TaskMapRules.Validate(map);
   if(map.Status is MapStatus.Canceled or MapStatus.Completed||map.Revision!=expectedRevision)throw new InvalidOperationException("The map changed or is closed. Reload before starting tasks.");
   if(string.IsNullOrWhiteSpace(_state.ValidationCommand))throw new InvalidOperationException("Save a validation command first.");
   UpdateMapStatuses();if(map.Revision!=expectedRevision)throw new InvalidOperationException("The map changed. Reload it.");
   var ids=selectedTaskIds.ToHashSet(StringComparer.Ordinal);var chosen=map.Tasks.Where(t=>ids.Contains(t.Id)).ToArray();
   if(chosen.Length!=ids.Count||chosen.Any(t=>t.WorkId!=null||t.WorkIds.Count!=0||_state.Work.Any(w=>w.ExternalRequestId==t.Id)||(map.Status==MapStatus.Active&&t.Selected)))throw new InvalidOperationException("Selected tasks must exist and be unstarted and unauthorised.");
   foreach(var edge in map.Edges.Where(e=>e.Kind==MapEdgeKind.Dependency&&ids.Contains(e.ToTaskId))){var parent=map.Tasks.Single(t=>t.Id==edge.FromTaskId);if(parent.Status!=MapTaskStatus.Completed&&!ids.Contains(parent.Id)&&!(map.Status==MapStatus.Active&&parent.Selected))throw new InvalidOperationException("Selected tasks must include or already authorize every unfinished dependency.");}
   new ContextArtifacts(_store.Root).Verify(map.ContextRefs);
   if(map.Status==MapStatus.Draft)foreach(var task in map.Tasks.Where(t=>t.WorkId==null))task.Selected=false;
   foreach(var task in chosen)task.Selected=true;map.Status=MapStatus.Active;map.Revision++;launched=LaunchReadyMapTasks(map);Save();
  }
  Changed?.Invoke();return Task.FromResult<IReadOnlyList<string>>(launched);
 }
 private static string MapPrompt(MapTask task) => task.Prompt.Trim() + "\n\n# Acceptance criteria for this task:\n# " + task.Acceptance.Trim().Replace("\n", "\n# ") + "\n# Report evidence that each criterion is met.";
 private List<string> LaunchReadyMapTasks(TaskMap map, IReadOnlySet<string>? onlyTaskIds = null)
 {
  var launched = new List<string>();
  if (_disposed || _updateDrain != null || map.Status != MapStatus.Active) return launched;
  foreach (var task in map.Tasks.Where(t => t.Selected && t.WorkId == null && (onlyTaskIds == null || onlyTaskIds.Contains(t.Id)) && TaskMapRules.DependenciesComplete(map, t)).ToArray())
  {
   // StartAsync persists the stable request ID before execution. Holding _sync also reserves the map node.
   new ContextArtifacts(_store.Root).Verify(map.ContextRefs);
   var id = StartAsync(MapPrompt(task), externalRequestId: task.Id, contextRefs: map.ContextRefs).GetAwaiter().GetResult();
   task.WorkId = id; if (!task.WorkIds.Contains(id)) task.WorkIds.Add(id);
   task.Status = MapTaskStatus.Running; map.Revision++; launched.Add(id);
  }
  return launched;
 }
 private void ScheduleSelectedMapTasks()
 {
  lock (_sync)
  {
   foreach (var map in _state.Maps.Where(m => m.Status == MapStatus.Active)) LaunchReadyMapTasks(map);
   Save();
  }
 }
 private static bool IsRevisionAttempt(WorkUnit work)=>work.Relationship==WorkRelationship.Revision||(work.Relationship==WorkRelationship.Unknown&&work.ParentId!=null&&(string.IsNullOrWhiteSpace(work.Task)||work.Task.Contains("This is a revision of earlier work.",StringComparison.Ordinal)));
 private void AddFollowupMapNode(WorkUnit work)
 {
  if(work.ParentId==null||work.Relationship!=WorkRelationship.Followup)return;
  var sourceId=work.ParentId;var seen=new HashSet<string>(StringComparer.Ordinal);
  while(seen.Add(sourceId)&&_state.Work.FirstOrDefault(w=>w.Id==sourceId) is {} source&&IsRevisionAttempt(source)&&source.ParentId!=null)sourceId=source.ParentId;
  foreach(var map in _state.Maps)
  {
   var parent=map.Tasks.FirstOrDefault(t=>t.WorkId==sourceId||t.WorkIds.Contains(sourceId));
   if(parent==null||map.Tasks.Any(t=>t.WorkId==work.Id))continue;
   var task=new MapTask{Title=work.ShortTask,Prompt=work.Task,Acceptance="Complete this followup and report evidence.",WorkId=work.Id,WorkIds=[work.Id],Selected=true,Status=MapTaskStatus.Running};
   map.Tasks.Add(task);map.Edges.Add(new MapEdge(parent.Id,task.Id,MapEdgeKind.Followup));map.Revision++;if(map.Status==MapStatus.Completed)map.Status=MapStatus.Active;
  }
 }
 private void UpdateMapStatuses()
 {
  foreach(var map in _state.Maps)
   foreach(var task in map.Tasks)
   {
    task.WorkIds ??=[];
    var root=_state.Work.FirstOrDefault(w=>w.ExternalRequestId==task.Id);
    if(task.WorkId==null&&root!=null)task.WorkId=root.Id;
   }
  foreach(var work in _state.Work.Where(w=>w.Relationship==WorkRelationship.Unknown))work.Relationship=work.ParentId==null?WorkRelationship.Original:IsRevisionAttempt(work)?WorkRelationship.Revision:WorkRelationship.Followup;
  foreach(var work in _state.Work.Where(w=>w.Relationship==WorkRelationship.Revision&&string.IsNullOrWhiteSpace(w.Title)))
   work.Title=RevisionTitle.FromReport(work.CodexReport)??_state.Work.FirstOrDefault(parent=>parent.Id==work.ParentId)?.ShortTask;
  foreach(var work in _state.Work.Where(w=>w.Relationship==WorkRelationship.Followup))AddFollowupMapNode(work);
  var interactions=_interactions.Inspect();
  foreach(var map in _state.Maps)
  {
   foreach(var task in map.Tasks)
   {
    task.RequiredDebtCount=0;
    if(task.WorkId==null)continue;
    var known=new HashSet<string>(StringComparer.Ordinal){task.WorkId};bool changed;
    do{changed=false;foreach(var child in _state.Work.Where(w=>w.ParentId!=null&&IsRevisionAttempt(w)&&known.Contains(w.ParentId)))if(known.Add(child.Id))changed=true;}while(changed);
    task.WorkIds=_state.Work.Where(w=>known.Contains(w.Id)).Select(w=>w.Id).ToList();
    task.RequiredDebtCount=interactions.Count(x=>known.Contains(x.WorkId)&&x.Required&&x.Status==InteractionStatus.Pending&&(x.Kind is InteractionKind.Obligation or InteractionKind.Followup));
    var attempts=_state.Work.Where(w=>known.Contains(w.Id)).ToArray();var latest=attempts.LastOrDefault(w=>w.Status==WorkStatus.Completed)??attempts.LastOrDefault();
    task.Status=latest?.Status switch{WorkStatus.Completed=>MapTaskStatus.Completed,WorkStatus.Private=>MapTaskStatus.Private,WorkStatus.Waiting=>MapTaskStatus.Waiting,WorkStatus.Validating=>MapTaskStatus.Validating,WorkStatus.Stale=>MapTaskStatus.Stale,WorkStatus.NeedsResponse=>MapTaskStatus.NeedsResponse,WorkStatus.Parked=>MapTaskStatus.Parked,WorkStatus.Abandoned=>MapTaskStatus.Abandoned,WorkStatus.Unknown=>MapTaskStatus.Unknown,WorkStatus.Failed=>MapTaskStatus.Failed,WorkStatus.Canceled=>MapTaskStatus.Canceled,_=>MapTaskStatus.Running};
   }
   foreach(var task in map.Tasks.Where(t=>t.WorkId==null))task.Status=map.Status==MapStatus.Canceled?MapTaskStatus.Canceled:TaskMapRules.DependenciesComplete(map,task)?MapTaskStatus.Ready:MapTaskStatus.Draft;
   map.RequiredDebtCount=map.Tasks.Sum(t=>t.RequiredDebtCount);
   if(map.Status==MapStatus.Completed&&map.RequiredDebtCount>0)map.Status=MapStatus.Active;
   if(map.Status==MapStatus.Active&&map.RequiredDebtCount==0&&map.Tasks.All(t=>t.Selected&&t.Status==MapTaskStatus.Completed))map.Status=MapStatus.Completed;
  }
 }
}