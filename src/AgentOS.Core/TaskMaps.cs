using System.Security.Cryptography;
namespace AgentOS.Core;
public enum MapStatus { Draft, Active, Completed, Canceled }
public enum MapEdgeKind { Dependency, Related, Followup }
public enum MapTaskStatus { Draft, Ready, Running, Completed, Failed, Canceled, Private, Waiting, Stale, Unknown, Validating }
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
  if (map.Tasks == null || map.Edges == null || map.Citations == null || map.Tasks.Count < 1) throw new ArgumentException("A map needs tasks and valid collections.");
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
   var map = JsonFormat.Copy(input); map.ProjectPath = _state.ProjectPath; map.Status = MapStatus.Draft;
   foreach (var task in map.Tasks) { task.WorkId = null; task.WorkIds = []; task.Status = MapTaskStatus.Draft; }
   map.Revision = (existing?.Revision ?? 0) + 1;
   if (existing != null)
   {
    if (!existing.Citations.SequenceEqual(map.Citations)) throw new InvalidOperationException("Captured citations cannot be replaced.");
    _state.Maps[_state.Maps.IndexOf(existing)] = map;
   }
   else _state.Maps.Add(map);
   id = map.Id; Event(null, "MapDraft", $"Saved map {id} revision {map.Revision}; no task was started."); Save();
  }
  Changed?.Invoke(); return id;
 }
 public Task<IReadOnlyList<string>> StartSelectedMapTasksAsync(string mapId)
 {
  ObjectDisposedException.ThrowIf(_disposed, this);
  List<string> launched;
  lock (_sync)
  {
   var map = _state.Maps.Single(m => m.Id == mapId); TaskMapRules.Validate(map);
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
 private static string MapPrompt(MapTask task) => task.Prompt.Trim() + "\n\n# Acceptance criteria for this task:\n# " + task.Acceptance.Trim().Replace("\n", "\n# ") + "\n# Report evidence that each criterion is met.";
 private List<string> LaunchReadyMapTasks(TaskMap map)
 {
  var launched = new List<string>();
  if (_disposed || map.Status != MapStatus.Active) return launched;
  foreach (var task in map.Tasks.Where(t => t.Selected && t.WorkId == null && TaskMapRules.DependenciesComplete(map, t)).ToArray())
  {
   // StartAsync persists the stable request ID before execution. Holding _sync also reserves the map node.
   var id = StartAsync(MapPrompt(task), externalRequestId: task.Id).GetAwaiter().GetResult();
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
 private void UpdateMapStatuses()
 {
  foreach (var map in _state.Maps)
  {
   foreach (var task in map.Tasks)
   {
    task.WorkIds ??= [];
    var root = _state.Work.FirstOrDefault(w => w.ExternalRequestId == task.Id);
    if (root != null && !task.WorkIds.Contains(root.Id)) task.WorkIds.Insert(0, root.Id);
    if (task.WorkId == null && root != null) task.WorkId = root.Id;
    if (task.WorkId == null) continue;
    var known = task.WorkIds.ToHashSet(StringComparer.Ordinal); known.Add(task.WorkId);
    bool changed;
    do
    {
     changed = false;
     foreach (var child in _state.Work.Where(w => w.ParentId != null && known.Contains(w.ParentId)))
      if (known.Add(child.Id)) changed = true;
    } while (changed);
    task.WorkIds = _state.Work.Where(w => known.Contains(w.Id)).Select(w => w.Id).ToList();
    var attempts = _state.Work.Where(w => known.Contains(w.Id)).ToArray();
    var successful = attempts.LastOrDefault(w => w.Status == WorkStatus.Completed);
    var latest = successful ?? attempts.LastOrDefault();
    task.Status = latest?.Status switch
    {
     WorkStatus.Completed => MapTaskStatus.Completed,
     WorkStatus.Private => MapTaskStatus.Private,
     WorkStatus.Waiting => MapTaskStatus.Waiting,
     WorkStatus.Validating => MapTaskStatus.Validating,
     WorkStatus.Stale => MapTaskStatus.Stale,
     WorkStatus.Unknown => MapTaskStatus.Unknown,
     WorkStatus.Failed => MapTaskStatus.Failed,
     WorkStatus.Canceled => MapTaskStatus.Canceled,
     _ => MapTaskStatus.Running
    };
   }
   foreach (var task in map.Tasks.Where(t => t.WorkId == null)) task.Status = map.Status == MapStatus.Canceled ? MapTaskStatus.Canceled : TaskMapRules.DependenciesComplete(map, task) ? MapTaskStatus.Ready : MapTaskStatus.Draft;
   if (map.Status == MapStatus.Active && map.Tasks.All(t => t.Selected && t.Status == MapTaskStatus.Completed)) map.Status = MapStatus.Completed;
  }
 }
}
