namespace AgentOS.Core;
public sealed partial class ProjectRuntime
{
    private async Task RecordConflict(WorkUnit work, string cause, IEnumerable<string> paths, string current)
    {
        var affected = paths.Distinct(StringComparer.Ordinal).ToList();
        WorkUnit? holder;
        lock (_sync) holder = _state.Work.LastOrDefault(x => x.Id != work.Id && x.IntegratedCommit != null && (x.Status == WorkStatus.Parked ? (IEnumerable<string>)x.PublishedPathObjects.Keys : x.ChangedPaths).Any(held => affected.Any(path => held.Equals(path, StringComparison.OrdinalIgnoreCase) || held.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) || path.StartsWith(held + "/", StringComparison.OrdinalIgnoreCase))));
        var notice = new ConflictNotice { WorkId = work.Id, Cause = cause, Paths = affected, BaseCommit = work.BaseCommit, CurrentCommit = current, HolderWorkId = holder?.Id, DeferredCandidateCommit = work.CandidateCommit };
        if (notice.DeferredCandidateCommit != null)
            (await Commands.Git(_state.ProjectPath, "update-ref", "refs/agent-os/deferred/" + notice.Id, notice.DeferredCandidateCommit, new string('0', notice.DeferredCandidateCommit.Length))).Checked();
        Mutate(() => { _state.Conflicts.Add(notice); work.Status = WorkStatus.NeedsResponse; work.Detail = $"Conflict {notice.Id}: {cause}. Publication blocked; candidate retained. Respond through respond_to_conflict."; work.UpdatedAt = DateTimeOffset.UtcNow; UpdateMapStatuses(); Event(work.Id, "Conflict", work.Detail); });
    }
    private ConflictNotice FindConflict(string workId, string conflictId)
    {
        lock (_sync) return _state.Conflicts.Single(x => x.Id == conflictId && x.WorkId == workId && !x.Resolved && !x.Abandoned);
    }
    public ConflictNotice RespondToConflict(string workId, string conflictId, string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) throw new ArgumentException("A free-form explanation is required.");
        var notice = FindConflict(workId, conflictId);
        Mutate(() => { if (notice.Response != null) throw new InvalidOperationException("Conflict already answered."); notice.Response = explanation.Trim(); Find(workId).ConflictNonresponses = 0; Event(workId, "ConflictResponse", $"Conflict {conflictId} received a response."); });
        return JsonFormat.Copy(notice);
    }
    public ConflictNotice ResolveConflict(string workId, string conflictId, string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) throw new ArgumentException("A reconciliation explanation is required.");
        var notice = FindConflict(workId, conflictId);
        if (notice.Response == null) throw new InvalidOperationException("Respond first.");
        Mutate(() => { notice.ResolutionRequested = true; notice.ResolutionExplanation = explanation.Trim(); Event(workId, "ConflictResolutionRequested", $"Reconciled {conflictId}: {explanation.Trim()}"); });
        return JsonFormat.Copy(notice);
    }
    public ConflictNotice AbandonConflict(string workId, string conflictId, string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) throw new ArgumentException("An explanation is required.");
        var notice = FindConflict(workId, conflictId);
        if (notice.Response == null) throw new InvalidOperationException("Respond first.");
        Mutate(() => { notice.Abandoned = true; notice.PublicationBlocked = false; var work = Find(workId); if (!_state.Conflicts.Any(x => x.WorkId == workId && !x.Resolved && !x.Abandoned)) { work.Status = WorkStatus.Abandoned; work.Detail = "Deferred work explicitly abandoned; candidate retained."; UpdateMapStatuses(); } Event(workId, "ConflictAbandoned", $"Abandoned {conflictId}: {explanation.Trim()}"); });
        return JsonFormat.Copy(notice);
    }
    internal static string ConflictPrompt(ConflictNotice n) => $"Conflict {n.Id}: {n.Cause}. Paths: {string.Join(", ", n.Paths)}. Base shared commit: {n.BaseCommit}. Current shared commit: {n.CurrentCommit}. Holder: {n.HolderWorkId ?? "unknown"}. Publication blocked. Original candidate: {n.DeferredCandidateCommit}. Continue in this same thread. Call respond_to_conflict with your own nonempty free-form explanation. Keep both agents focused and uninterrupted where possible. If coordination is needed, use a coordinator peer message or exact force-interrupt before asking the human. Escalate to the human when this cause cannot be reconciled with your task. Do not auto-select an outcome. A final answer while a response is owed remains blocked; resume this same thread.";
    internal static string ContinuationPrompt(ConflictNotice n) => n.Response == null ? ConflictPrompt(n) : $"Conflict {n.Id} remains parked; candidate {n.DeferredCandidateCommit} retained. Reconcile source and call agent_os_resolve_conflict, or call agent_os_abandon_conflict. Independent work may continue.";
    public PeerMessage SendPeerMessage(string fromWorkId, string toWorkId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Peer message is required.");
        var sender = Find(fromWorkId); var target = Find(toWorkId);
        if (!sender.IsActive && !(_jobs.TryGetValue(fromWorkId, out var senderJob) && !senderJob.IsCompleted)) throw new InvalidOperationException("Sender is not an active managed task.");
        if (target.Status is WorkStatus.Completed or WorkStatus.Canceled or WorkStatus.Failed or WorkStatus.Abandoned) throw new InvalidOperationException("Target task has ended.");
        var item = new PeerMessage { FromWorkId = fromWorkId, ToWorkId = toWorkId, Text = message.Trim() };
        Mutate(() => { _state.PeerMessages.Add(item); Event(fromWorkId, "PeerMessage", $"Queued {item.Id} for {toWorkId}."); });
        return JsonFormat.Copy(item);
    }
    public IReadOnlyList<PeerMessage> PendingPeerMessages(string toWorkId)
    {
        lock (_sync) return _state.PeerMessages.Where(x => x.ToWorkId == toWorkId && x.DeliveredAt == null).Select(x => JsonFormat.Copy(x)).ToArray();
    }
    internal void MarkPeerDelivered(string toWorkId, string messageId)
    {
        Mutate(() => { var item = _state.PeerMessages.Single(x => x.Id == messageId && x.ToWorkId == toWorkId); item.DeliveredAt ??= DateTimeOffset.UtcNow; Event(toWorkId, "PeerDelivered", $"Message {messageId} delivered."); });
    }
    public InterruptRequest InterruptManagedTask(string fromWorkId, string targetWorkId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Interrupt reason is required.");
        var sender = Find(fromWorkId); var target = Find(targetWorkId);
        if (!sender.IsActive && !(_jobs.TryGetValue(fromWorkId, out var senderJob) && !senderJob.IsCompleted)) throw new InvalidOperationException("Requester is not an active managed task.");
        if ((!target.IsActive && !(_jobs.TryGetValue(targetWorkId, out var activeJob) && !activeJob.IsCompleted)) || !_tokens.ContainsKey(targetWorkId)) throw new InvalidOperationException("Target is not an active managed task.");
        var item = new InterruptRequest { FromWorkId = fromWorkId, TargetWorkId = targetWorkId, Reason = reason.Trim() };
        Mutate(() => { _state.InterruptRequests.Add(item); Event(fromWorkId, "Interrupt", $"Requested interruption of {targetWorkId}: {item.Reason}"); });
        _tokens[targetWorkId].Cancel(); return JsonFormat.Copy(item);
    }
    public HumanEscalation EscalateConflict(string workId, string conflictId, string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) throw new ArgumentException("Escalation explanation is required.");
        var notice = FindConflict(workId, conflictId);
        if (notice.Response == null) throw new InvalidOperationException("Respond first.");
        var item = new HumanEscalation { WorkId = workId, ConflictId = notice.Id, Explanation = explanation.Trim() };
        Mutate(() => { _state.Escalations.Add(item); Event(workId, "HumanEscalation", item.Explanation); });
        return JsonFormat.Copy(item);
    }
    internal void RecordHostThread(string workId, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidDataException("Codex did not provide a thread identity.");
        Mutate(() => Find(workId).ThreadId = threadId);
    }
    internal int RegisterConflictNonresponse(string workId)
    {
        var work = Find(workId);
        Mutate(() => { work.ConflictNonresponses++; Event(workId, "ConflictNonresponse", $"Response owed after {work.ConflictNonresponses} turns."); });
        return work.ConflictNonresponses;
    }
    private async Task StageResolvedPaths(WorkUnit work, HashSet<string> resolvedPaths, Dictionary<string, string> env, CancellationToken token)
    {
        foreach (var path in resolvedPaths.Where(work.ChangedPaths.Contains))
        {
            var entry = (await Commands.Git(_state.ProjectPath, "ls-tree", "-z", work.CandidateCommit!, "--", path)).Checked();
            CommandResult update;
            if (entry.Length == 0) update = await Commands.RunAsync("git", ["update-index", "--force-remove", "--", path], _state.ProjectPath, token, environment: env);
            else
            {
                var header = entry.Split('\t', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (header.Length != 3 || header[1] != "blob") throw new InvalidDataException("Resolved path is not a regular blob.");
                update = await Commands.RunAsync("git", ["update-index", "--add", "--cacheinfo", header[0], header[2], path], _state.ProjectPath, token, environment: env);
            }
            update.Checked();
        }
    }
    internal async Task<string?> AfterManagedTurnAsync(string workId, CancellationToken token)
    {
        var work = Find(workId);
        if(work.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,work.Id)){MarkSdkUnknown(work.Id);return null;}
        await Capture(work);
        var deferred = Snapshot.Conflicts.Where(x => x.WorkId == work.Id && !x.Resolved).ToArray();
        var unresolved = deferred.Where(x => !x.Abandoned).ToArray();
        var owed = unresolved.FirstOrDefault(x => x.Response == null);
        if (owed != null)
        {
            Set(work, WorkStatus.NeedsResponse, "A conflict response is owed. Candidate remains private.");
            return ConflictPrompt(owed);
        }
        foreach (var notice in unresolved.Where(x => x.ResolutionRequested))
        {
            foreach (var path in notice.Paths)
            {
                var original = (await Commands.Git(_state.ProjectPath, "ls-tree", "-z", notice.DeferredCandidateCommit!, "--", path)).Checked();
                var revised = (await Commands.Git(_state.ProjectPath, "ls-tree", "-z", work.CandidateCommit!, "--", path)).Checked();
                if (original == revised)
                {
                    Mutate(() => { FindConflict(workId, notice.Id).ResolutionRequested = false; Event(workId, "ConflictResolutionRejected", $"Reconciled source for {path} was unchanged from the blocked candidate."); });
                    Set(work, WorkStatus.Parked, "Resolution requires changed source for each blocked path; candidate retained.");
                    return ContinuationPrompt(notice);
                }
            }
        }
        var excluded = deferred.Where(x => x.Abandoned || !x.ResolutionRequested).SelectMany(x => x.Paths).ToHashSet(StringComparer.Ordinal);
        var allPaths = work.ChangedPaths.ToList();
        var alreadyPublished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in allPaths)
            if (work.PublishedPathObjects.TryGetValue(path, out var identity) && identity == (await Commands.Git(_state.ProjectPath, "ls-tree", "-z", work.CandidateCommit!, "--", path)).Checked())
                alreadyPublished.Add(path);
        var independent = allPaths.Where(x => !excluded.Contains(x) && !alreadyPublished.Contains(x)).ToList();
        if (deferred.Length > 0 && independent.Count == 0)
        {
            Set(work, unresolved.Length > 0 ? WorkStatus.Parked : WorkStatus.Abandoned, "Deferred source retained without publication.");
            return null;
        }
        if (!work.AutoIntegrate)
        {
            Set(work, unresolved.Length > 0 ? WorkStatus.Parked : WorkStatus.Private, "Private candidate retained.");
            return null;
        }
        work.ChangedPaths = independent;
        try { await IntegrateCore(work, token); }
        finally { Mutate(() => work.ChangedPaths = allPaths); }
        if (work.Status is WorkStatus.Completed or WorkStatus.Parked)
        {
            var published = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in independent) published[path] = (await Commands.Git(_state.ProjectPath, "ls-tree", "-z", work.CandidateCommit!, "--", path)).Checked();
            Mutate(() => { foreach (var pair in published) work.PublishedPathObjects[pair.Key] = pair.Value; });
        }
        if (Snapshot.Conflicts.Any(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned) && work.Status == WorkStatus.Completed)
            Set(work, WorkStatus.Parked, "Independent edits published; original conflicting work remains deferred.");
        owed = Snapshot.Conflicts.FirstOrDefault(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned && x.Response == null);
        return owed == null ? null : ConflictPrompt(owed);
    }
    public Task ResumeConflictAsync(string workId)
    {
        var work = Find(workId);
        lock (_sync)
        {
            RequireUpdateAdmission();
            if (work.Status is not (WorkStatus.NeedsResponse or WorkStatus.Parked) || !_state.Conflicts.Any(x => x.WorkId == workId && !x.Resolved && !x.Abandoned))
                throw new InvalidOperationException("No unresolved conflict to resume.");
            if (string.IsNullOrWhiteSpace(work.ThreadId)) throw new InvalidOperationException("Original Codex thread is unavailable.");
            if (_host is not ManagedCodexHost) throw new InvalidOperationException("Only managed Codex can resume its thread.");
            if (_jobs.TryGetValue(workId, out var old) && !old.IsCompleted) throw new InvalidOperationException("Work is still running.");
            var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _tokens[workId] = source;
            return _jobs[workId] = Task.Run(async () =>
            {
                try
                {
                    var result = await _host.Run(work, _state.CodexPath, LogPath(work, "codex-resumed-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                        line => Mutate(() => Event(work.Id, "Codex", line.Length > 3000 ? line[..3000] : line)), source.Token);
                    Mutate(() => { work.ThreadId = result.ThreadId; work.CodexReport = result.Report; work.HostModel = result.Model; });
                    if (!result.TurnCompleted || result.ExitCode != 0) Set(work, WorkStatus.NeedsResponse, "Resumed thread ended without a confirmed turn; candidate and response obligation retained.");
                }
                catch (OperationCanceledException) { Set(work, _state.Conflicts.Any(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned && x.Response == null) ? WorkStatus.NeedsResponse : WorkStatus.Parked, "Conflict continuation canceled; candidate retained."); }
                catch (Exception error) { Set(work, _state.Conflicts.Any(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned && x.Response == null) ? WorkStatus.NeedsResponse : WorkStatus.Parked, "Conflict continuation failed: " + error.Message + " Candidate retained."); }
            });
        }
    }
}
