using System.Text.Json;
namespace AgentOS.Core;
public enum InteractionStatus { Queued, Delivered, Acknowledged, Rejected, Pending, Replied, Canceled, Expired, Resolved }
public enum InteractionKind { Steering, Clarification, Peer, Followup, Obligation, Wait }
public enum WaitKind { Task, Message, Decision, Resource }
public sealed class TaskInteraction
{
 public string Id { get; set; } = Guid.NewGuid().ToString("N");
 public string WorkId { get; set; } = "";
 public InteractionKind Kind { get; set; }
 public InteractionStatus Status { get; set; }
 public string Text { get; set; } = "";
 public string? Response { get; set; }
 public string? TargetWorkId { get; set; }
 public string? Scope { get; set; }
 public string? TurnId { get; set; }
 public DateTimeOffset? DeliveredAt { get; set; }
 public string? RelatedId { get; set; }
 public bool Required { get; set; }
 public WaitKind? WaitKind { get; set; }
 public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
 public DateTimeOffset? Deadline { get; set; }
 public DateTimeOffset? UpdatedAt { get; set; }
}
public sealed class TaskInteractionStore
{
 private sealed class Journal { public int Schema { get; set; } = 1; public List<TaskInteraction> Items { get; set; } = []; }
 private readonly object _gate = new();
 private readonly string _path;
 private readonly Journal _journal;
 private readonly Dictionary<string, TaskCompletionSource<TaskInteraction>> _pending = [];
 public TaskInteractionStore(string root)
 {
  Directory.CreateDirectory(root); _path = Path.Combine(root, "interactions.json");
  _journal = File.Exists(_path) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(_path), JsonFormat.Options) ?? throw new InvalidDataException("Empty interaction journal.") : new();
  if (_journal.Schema != 1 || _journal.Items == null || _journal.Items.Select(x => x.Id).Distinct().Count() != _journal.Items.Count) throw new InvalidDataException("Invalid interaction journal.");
 }
 public IReadOnlyList<TaskInteraction> Inspect(string? workId = null) { lock (_gate) { ExpireDue(); return JsonFormat.Copy(_journal.Items.Where(x => workId == null || x.WorkId == workId || x.TargetWorkId == workId).ToList()); } }
 public TaskInteraction Get(string id) { lock (_gate) { ExpireDue(); return JsonFormat.Copy(Find(id)); } }
 private TaskInteraction Find(string id) => _journal.Items.Single(x => x.Id == id);
 private void ExpireDue()
 {
  var due = _journal.Items.Where(x => x.Status == InteractionStatus.Pending && x.Deadline.HasValue && x.Deadline.Value <= DateTimeOffset.UtcNow).ToArray();
  if (due.Length == 0) return;
  foreach (var item in due) { item.Status = InteractionStatus.Expired; item.Response = "Deadline expired."; item.UpdatedAt = DateTimeOffset.UtcNow; if (_pending.Remove(item.Id, out var waiter)) waiter.TrySetResult(JsonFormat.Copy(item)); }
  Save();
 }
 private void Save()
 {
  var temp = _path + ".new";
  using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, _journal, JsonFormat.Options); file.Flush(true); }
  for (var attempt = 0; ; attempt++)
  {
   try { File.Move(temp, _path, true); break; }
   catch (IOException) when (attempt < 20) { Thread.Sleep(10); }
   catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(10); }
  }
 }
 public TaskInteraction Add(TaskInteraction item)
 {
  lock (_gate) { if (_journal.Items.Any(x => x.Id == item.Id)) throw new ArgumentException("Duplicate interaction ID."); _journal.Items.Add(item); Save(); return JsonFormat.Copy(item); }
 }
 public TaskInteraction AddFollowup(string workId,string task,bool required)
 {
  lock(_gate)
  {
   var obligation=required?new TaskInteraction{WorkId=workId,Kind=InteractionKind.Obligation,Status=InteractionStatus.Pending,Required=true,Text=task}:null;
   var proposal=new TaskInteraction{WorkId=workId,Kind=InteractionKind.Followup,Status=InteractionStatus.Pending,Required=required,Text=task,RelatedId=obligation?.Id};
   if(obligation!=null)_journal.Items.Add(obligation);_journal.Items.Add(proposal);Save();return JsonFormat.Copy(proposal);
  }
 } public TaskInteraction Change(string id, InteractionStatus status, string? response = null, string? turnId = null)
 {
  lock (_gate)
  {
   ExpireDue(); var item = Find(id); if (item.Status is InteractionStatus.Canceled or InteractionStatus.Expired or InteractionStatus.Rejected or InteractionStatus.Resolved or InteractionStatus.Acknowledged or InteractionStatus.Replied) throw new InvalidOperationException("Interaction is already terminal."); item.Status = status; item.UpdatedAt = DateTimeOffset.UtcNow;
   if (response != null) item.Response = response; if (turnId != null) { item.TurnId = turnId; item.DeliveredAt = DateTimeOffset.UtcNow; }
   Save(); if (status != InteractionStatus.Pending && _pending.Remove(id, out var waiter)) waiter.TrySetResult(JsonFormat.Copy(item)); return JsonFormat.Copy(item);
  }
 }
 public async Task<TaskInteraction> AwaitReplyAsync(string id, CancellationToken cancel)
 {
  Task<TaskInteraction> task;
  lock (_gate)
  {
   var item = Find(id);
   ExpireDue(); if (item.Status != InteractionStatus.Pending) return JsonFormat.Copy(item);
   if (!_pending.TryGetValue(id, out var completion)) _pending[id] = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
   task = completion.Task;
  }
  try { return await task.WaitAsync(cancel); }
  catch (OperationCanceledException) { var current = Get(id); return current.Status == InteractionStatus.Pending ? Change(id, InteractionStatus.Canceled, "The request was canceled.") : current; }
 }
 public TaskInteraction Reassign(string id, string oldTarget, string newTarget)
 {
  lock (_gate)
  {
   ExpireDue(); var item = Find(id);
   if (item.Kind != InteractionKind.Peer || item.Status != InteractionStatus.Pending || item.TargetWorkId != oldTarget) throw new InvalidOperationException("Peer request cannot be handed off.");
   item.TargetWorkId = newTarget; item.TurnId = null; item.DeliveredAt = null; item.Response = "Handed off from " + oldTarget; item.UpdatedAt = DateTimeOffset.UtcNow; Save(); return JsonFormat.Copy(item);
  }
 } public void Recover(Func<string, bool> alive)
 {
  lock (_gate)
  {
   var changed = false;
   foreach (var item in _journal.Items)
   {
    if (item.Status == InteractionStatus.Queued && item.Kind == InteractionKind.Steering && !alive(item.WorkId)) { item.Status = InteractionStatus.Rejected; item.Response = "The original turn ended before delivery; no turn was replayed."; changed = true; }
    if (item.Status == InteractionStatus.Pending && (item.Kind is InteractionKind.Clarification or InteractionKind.Wait or InteractionKind.Peer) && !alive(item.WorkId)) { item.Status = InteractionStatus.Canceled; item.Response = "The owning turn stopped."; changed = true; }
    if (item.Status == InteractionStatus.Pending && item.Deadline.HasValue && item.Deadline.Value <= DateTimeOffset.UtcNow) { item.Status = InteractionStatus.Expired; item.Response = "Deadline expired."; changed = true; }
   }
   if (changed) Save();
  }
 }
}
public sealed partial class ProjectRuntime
{
 public IReadOnlyList<TaskInteraction> InspectInteractions(string? workId = null)
 {
  if (workId != null) Find(workId);
  RefreshWaits();
  return _interactions.Inspect(workId);
 }
 private void SetIfPending(string id, InteractionStatus status, string response)
 {
  try { _interactions.Change(id, status, response); } catch (InvalidOperationException) { }
 }
 private void RefreshWaits()
 {
  var all = _interactions.Inspect();
  foreach (var wait in all.Where(x => x.Kind == InteractionKind.Wait && x.Status == InteractionStatus.Pending))
  {
   if (wait.WaitKind == WaitKind.Task && wait.TargetWorkId != null && !Find(wait.TargetWorkId).IsActive)
    SetIfPending(wait.Id, InteractionStatus.Resolved, "Task ended: " + Find(wait.TargetWorkId).Status);
   else if (wait.WaitKind == WaitKind.Message)
   {
    var message = all.SingleOrDefault(x => x.Id == wait.RelatedId);
    if (message != null && message.Status is InteractionStatus.Acknowledged or InteractionStatus.Replied or InteractionStatus.Rejected or InteractionStatus.Canceled)
     SetIfPending(wait.Id, InteractionStatus.Resolved, message.Status + ": " + message.Response);
   }
   else if (wait.WaitKind == WaitKind.Decision)
   {
    var decision = Snapshot.Decisions.SingleOrDefault(x => x.Id == wait.RelatedId);
    if (decision != null && decision.Status != DecisionStatus.Pending) SetIfPending(wait.Id, InteractionStatus.Resolved, decision.Status.ToString());
   }
  }
 }
 public Task<TaskInteraction> SendSteeringAsync(string workId, string text)
 {
  var work = Find(workId); if (!work.IsActive) throw new InvalidOperationException("The task is no longer active. Start an authorized followup turn.");
  if (string.IsNullOrWhiteSpace(text)||text.Length>16000) throw new ArgumentException("Steering text must contain 1 to 16000 characters.");
  return Task.FromResult(_interactions.Add(new TaskInteraction { WorkId = workId, Kind = InteractionKind.Steering, Status = InteractionStatus.Queued, Text = text.Trim() }));
 }
 public Task<TaskInteraction> ReplyClarificationAsync(string workId, string requestId, string response)
 {
  Find(workId); var item = _interactions.Get(requestId);
  if (item.WorkId != workId || item.Kind != InteractionKind.Clarification || item.Status != InteractionStatus.Pending) throw new InvalidOperationException("No pending clarification in this task scope.");
  if (string.IsNullOrWhiteSpace(response)) throw new ArgumentException("A reply is required.");
  return Task.FromResult(_interactions.Change(requestId, InteractionStatus.Replied, response));
 }
 public TaskInteraction AskPeer(string workId, string targetWorkId, string question, DateTimeOffset? deadline = null)
 {
  var sender = Find(workId); var target = Find(targetWorkId);
  if (!sender.IsActive || !target.IsActive) throw new InvalidOperationException("Both peer tasks must be active.");
  if (workId == targetWorkId) throw new ArgumentException("A task cannot ask itself.");
  if (string.IsNullOrWhiteSpace(question)||question.Length>16000) throw new ArgumentException("A peer request needs 1 to 16000 characters.");
  if (deadline.HasValue && (deadline.Value <= DateTimeOffset.UtcNow || deadline.Value > DateTimeOffset.UtcNow.AddDays(30))) throw new ArgumentException("Peer deadline has passed.");
  return _interactions.Add(new TaskInteraction { WorkId = workId, TargetWorkId = targetWorkId, Kind = InteractionKind.Peer, Status = InteractionStatus.Pending, Text = question, Deadline = deadline });
 }
 public TaskInteraction AcknowledgePeer(string targetWorkId, string requestId, string response)
 {
  if(!Find(targetWorkId).IsActive)throw new InvalidOperationException("Peer recipient is no longer active."); var item = _interactions.Get(requestId);
  if (item.TargetWorkId != targetWorkId || item.Kind != InteractionKind.Peer || item.Status != InteractionStatus.Pending || string.IsNullOrWhiteSpace(response) || response.Length>16000) throw new InvalidOperationException("No pending peer request or valid response for this task.");
  return _interactions.Change(requestId, InteractionStatus.Acknowledged, response);
 }
 public TaskInteraction HandoffPeer(string targetWorkId, string requestId, string newTargetWorkId)
 {
  var current = Find(targetWorkId); var next = Find(newTargetWorkId);
  if (!current.IsActive || !next.IsActive || targetWorkId == newTargetWorkId) throw new InvalidOperationException("Peer handoff requires another active task.");
  return _interactions.Reassign(requestId, targetWorkId, newTargetWorkId);
 }
 public TaskInteraction ResolveWait(string workId, string waitId, string resolution)
 {
  Find(workId); var item = _interactions.Get(waitId);
  if (item.WorkId != workId || item.Kind != InteractionKind.Wait || item.Status != InteractionStatus.Pending) throw new InvalidOperationException("No pending wait for this task.");
  if (item.WaitKind != WaitKind.Resource) throw new InvalidOperationException("Only resource waits require explicit resolution.");
  if(string.IsNullOrWhiteSpace(resolution)||resolution.Length>16000)throw new ArgumentException("Resource resolution must contain 1 to 16000 characters.");
  return _interactions.Change(waitId, InteractionStatus.Resolved, resolution.Trim());
 }
 public IReadOnlyList<TaskInteraction> Inbox(string workId,string? turnId=null)
 {
  Find(workId);return _interactions.Inspect(workId).Where(x=>(x.WorkId==workId||x.TargetWorkId==workId)&&(x.Kind!=InteractionKind.Steering||(x.WorkId==workId&&x.TurnId==turnId&&turnId!=null&&x.Status is InteractionStatus.Delivered or InteractionStatus.Acknowledged))).OrderByDescending(x=>x.CreatedAt).Take(50).ToArray();
 }
 public TaskInteraction AcknowledgeMessage(string workId,string turnId,string messageId)
 {
  Find(workId);var item=_interactions.Get(messageId);if(item.WorkId!=workId||item.Kind!=InteractionKind.Steering||item.Status!=InteractionStatus.Delivered||item.TurnId!=turnId||string.IsNullOrWhiteSpace(turnId))throw new InvalidOperationException("No delivered steering message in this task turn.");return _interactions.Change(messageId,InteractionStatus.Acknowledged,"Explicit agent acknowledgement.");
 }
 public TaskInteraction ProposeFollowup(string workId,string task,bool required)
 {
  if(string.IsNullOrWhiteSpace(task)||task.Length>16000)throw new ArgumentException("Followup task must contain 1 to 16000 characters.");TaskInteraction proposal;
  lock(_sync){if(!Find(workId).IsActive)throw new InvalidOperationException("Only an active task can propose followup work.");proposal=_interactions.AddFollowup(workId,task.Trim(),required);UpdateMapStatuses();Save();}
  Changed?.Invoke();return proposal;
 }
 public Task<string> AcceptFollowup(string workId,string proposalId)
 {
  string id;lock(_sync){Find(workId);var item=_interactions.Get(proposalId);if(item.WorkId!=workId||item.Kind!=InteractionKind.Followup||item.Status!=InteractionStatus.Pending)throw new InvalidOperationException("No pending followup proposal.");id=StartAsync(item.Text,parentId:workId,externalRequestId:item.Id).GetAwaiter().GetResult();_interactions.Change(item.Id,InteractionStatus.Acknowledged,id);UpdateMapStatuses();Save();}Changed?.Invoke();return Task.FromResult(id);
 }
 public TaskInteraction RejectFollowup(string workId,string proposalId,string reason)
 {
  if(string.IsNullOrWhiteSpace(reason))throw new ArgumentException("A rejection reason is required.");TaskInteraction rejected;
  lock(_sync){Find(workId);var item=_interactions.Get(proposalId);if(item.WorkId!=workId||item.Kind!=InteractionKind.Followup||item.Status!=InteractionStatus.Pending)throw new InvalidOperationException("No pending followup proposal.");rejected=_interactions.Change(proposalId,InteractionStatus.Rejected,reason.Trim());UpdateMapStatuses();Save();}Changed?.Invoke();return rejected;
 } public TaskInteraction ResolveObligation(string workId, string obligationId, string resolution)
 {
  Find(workId); var item = _interactions.Get(obligationId);
  if (item.WorkId != workId || item.Kind != InteractionKind.Obligation || item.Status != InteractionStatus.Pending) throw new InvalidOperationException("No pending obligation.");
  if (string.IsNullOrWhiteSpace(resolution)) throw new ArgumentException("A resolution is required.");
  var settled=_interactions.Change(obligationId,InteractionStatus.Resolved,resolution);lock(_sync){UpdateMapStatuses();Save();}Changed?.Invoke();return settled;
 }
 public TaskInteraction CreateWait(string workId, WaitKind kind, string targetId, DateTimeOffset? deadline = null)
 {
  if (!Find(workId).IsActive) throw new InvalidOperationException("Only active tasks can create waits."); if (string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("Wait target is required.");
  if (kind == WaitKind.Message && !_interactions.Inspect().Any(x => x.Id == targetId)) throw new ArgumentException("Message wait target does not exist.");
  if (kind == WaitKind.Decision && !Snapshot.Decisions.Any(x => x.Id == targetId)) throw new ArgumentException("Decision wait target does not exist.");
  if (deadline <= DateTimeOffset.UtcNow) throw new ArgumentException("Wait deadline has passed.");
  if (kind == WaitKind.Task)
  {
   Find(targetId);
   if (targetId == workId) throw new InvalidOperationException("A task cannot wait on itself.");
   var graph = _interactions.Inspect().Where(x => x.Kind == InteractionKind.Wait && x.WaitKind == WaitKind.Task && x.Status == InteractionStatus.Pending).ToLookup(x => x.WorkId, x => x.TargetWorkId);
   bool Reaches(string current, HashSet<string> seen) => current == workId || (seen.Add(current) && graph[current].Any(next => next != null && Reaches(next, seen)));
   if (Reaches(targetId, [])) throw new InvalidOperationException("Wait dependency cycle.");
  }
  var wait = _interactions.Add(new TaskInteraction { WorkId = workId, TargetWorkId = kind == WaitKind.Task ? targetId : null, RelatedId = targetId, Kind = InteractionKind.Wait, WaitKind = kind, Status = InteractionStatus.Pending, Deadline = deadline });
  if (kind == WaitKind.Task && !Find(targetId).IsActive) return _interactions.Change(wait.Id, InteractionStatus.Resolved, "Task ended: " + Find(targetId).Status);
  if (kind == WaitKind.Message)
  {
   var message = _interactions.Get(targetId);
   if (message.Status is InteractionStatus.Acknowledged or InteractionStatus.Replied or InteractionStatus.Rejected or InteractionStatus.Canceled) return _interactions.Change(wait.Id, InteractionStatus.Resolved, message.Status + ": " + message.Response);
  }
  if (kind == WaitKind.Decision)
  {
   var decision = Snapshot.Decisions.Single(x => x.Id == targetId);
   if (decision.Status != DecisionStatus.Pending) return _interactions.Change(wait.Id, InteractionStatus.Resolved, decision.Status.ToString());
  }
  return wait;
 }
 public TaskInteraction CancelWait(string workId, string waitId)
 {
  Find(workId); var item = _interactions.Get(waitId);
  if (item.WorkId != workId || item.Kind != InteractionKind.Wait || item.Status != InteractionStatus.Pending) throw new InvalidOperationException("No pending wait for this task.");
  return _interactions.Change(waitId, InteractionStatus.Canceled, "Canceled by owner.");
 }
 public Task<string> ReplyAfterCompletionAsync(string workId, string text, string? requestId = null)
 {
  var original = Find(workId); if (original.Status != WorkStatus.Completed) throw new InvalidOperationException("Reply-after requires a completed task; use a new task for other states.");
  return StartAsync(text, parentId: workId, externalRequestId: requestId);
 }
 private void CancelOwnedInteractions(string workId)
 {
  foreach (var item in _interactions.Inspect(workId).Where(x => x.WorkId == workId && x.Status == InteractionStatus.Pending && (x.Kind is InteractionKind.Clarification or InteractionKind.Wait or InteractionKind.Peer)))
   SetIfPending(item.Id, InteractionStatus.Canceled, "The task stopped.");
 }
}

public sealed partial class ProjectRuntime
{
 private void OnInteractionOwnerEnded(string workId)
 {
  foreach (var item in _interactions.Inspect().Where(x => x.Status == InteractionStatus.Pending))
  {
   if (item.WorkId == workId && (item.Kind is InteractionKind.Clarification or InteractionKind.Wait or InteractionKind.Peer))
    SetIfPending(item.Id, InteractionStatus.Canceled, "The owning task ended.");
   else if (item.TargetWorkId == workId && item.Kind == InteractionKind.Peer)
    SetIfPending(item.Id, InteractionStatus.Rejected, "The peer task ended without acknowledgement.");
   else if (item.TargetWorkId == workId && item.Kind == InteractionKind.Wait && item.WaitKind == WaitKind.Task)
    SetIfPending(item.Id, InteractionStatus.Resolved, "Task ended: " + Find(workId).Status);
  }
 }
 private TaskInteraction CompleteWaitIfPending(string id, string response)
 {
  try { return _interactions.Change(id, InteractionStatus.Resolved, response); } catch (InvalidOperationException) { return _interactions.Get(id); }
 }
 public async Task<TaskInteraction> AwaitWaitAsync(string workId, string waitId, CancellationToken cancel = default)
 {
  Find(workId); var item = _interactions.Get(waitId);
  if (item.WorkId != workId || item.Kind != InteractionKind.Wait) throw new InvalidOperationException("Wait does not belong to this task.");
  while (item.Status == InteractionStatus.Pending)
  {
   cancel.ThrowIfCancellationRequested();
   if (item.WaitKind == WaitKind.Task && item.TargetWorkId != null && !Find(item.TargetWorkId).IsActive)
    return CompleteWaitIfPending(waitId, "Task ended: " + Find(item.TargetWorkId).Status);
   if (item.WaitKind == WaitKind.Message)
   {
    var related = _interactions.Inspect().SingleOrDefault(x => x.Id == item.RelatedId);
    if (related != null && related.Status is InteractionStatus.Acknowledged or InteractionStatus.Replied or InteractionStatus.Rejected or InteractionStatus.Canceled)
     return CompleteWaitIfPending(waitId, related.Status + ": " + related.Response);
   }
   if (item.WaitKind == WaitKind.Decision)
   {
    var decision = Snapshot.Decisions.SingleOrDefault(x => x.Id == item.RelatedId);
    if (decision != null && decision.Status != DecisionStatus.Pending) return CompleteWaitIfPending(waitId, decision.Status.ToString());
   }
   await Task.Delay(150, cancel);
   item = _interactions.Get(waitId);
  }
  return item;
 }
}
