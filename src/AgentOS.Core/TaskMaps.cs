using System.Security.Cryptography;
namespace AgentOS.Core;
public enum MapStatus { Draft, Active, Completed, Canceled }
public enum MapEdgeKind { Dependency, Related, Followup }
public enum MapTaskStatus { Draft, Ready, Running, Completed, Failed, Canceled }
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
  if (map.Tasks == null || map.Edges == null || map.Citations == null || map.Tasks.Count is < 1 or > 8) throw new ArgumentException("A map needs one to eight tasks and valid collections.");
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
   foreach (var task in map.Tasks) { task.WorkId = null; task.Status = MapTaskStatus.Draft; }
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
 public async Task<IReadOnlyList<string>> StartSelectedMapTasksAsync(string mapId)
 {
  List<(string TaskId, string Prompt)> ready;
  lock (_sync)
  {
   var map = _state.Maps.Single(m => m.Id == mapId); TaskMapRules.Validate(map);
   if (map.Status is MapStatus.Canceled or MapStatus.Completed) throw new InvalidOperationException("The map is closed.");
   ready = map.Tasks.Where(t => t.Selected && t.WorkId == null && TaskMapRules.DependenciesComplete(map, t)).Select(t => (t.Id, t.Prompt)).ToList();
   if (ready.Count == 0) throw new InvalidOperationException("Select a ready task; dependencies must finish first.");
  }
  var launched = new List<string>();
  foreach (var (taskId, prompt) in ready)
  {
   var id = await StartAsync(prompt, externalRequestId: taskId);
   lock (_sync)
   {
    var map = _state.Maps.Single(m => m.Id == mapId); var task = map.Tasks.Single(t => t.Id == taskId);
    task.WorkId = id; task.Status = MapTaskStatus.Running; map.Status = MapStatus.Active; map.Revision++; UpdateMapStatuses(); Save();
   }
   launched.Add(id);
  }
  Changed?.Invoke(); return launched;
 }
 private void UpdateMapStatuses()
 {
  foreach (var map in _state.Maps)
  {
   foreach (var task in map.Tasks)
   {
    if (task.WorkId == null) task.WorkId = _state.Work.SingleOrDefault(w => w.ExternalRequestId == task.Id)?.Id;
   }
   foreach (var task in map.Tasks.Where(t => t.WorkId != null))
   {
    var work = _state.Work.SingleOrDefault(w => w.Id == task.WorkId);
    task.Status = work?.Status switch
    {
     WorkStatus.Completed => MapTaskStatus.Completed,
     WorkStatus.Canceled => MapTaskStatus.Canceled,
     WorkStatus.Failed or WorkStatus.Stale or WorkStatus.Unknown => MapTaskStatus.Failed,
     _ => MapTaskStatus.Running
    };
   }
   foreach (var task in map.Tasks.Where(t => t.WorkId == null)) task.Status = TaskMapRules.DependenciesComplete(map, task) ? MapTaskStatus.Ready : MapTaskStatus.Draft;
   if (map.Tasks.Count > 0 && map.Tasks.All(t => t.Status == MapTaskStatus.Completed)) map.Status = MapStatus.Completed;
  }
 }
}

