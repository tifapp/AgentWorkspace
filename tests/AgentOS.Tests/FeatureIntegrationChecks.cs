using AgentOS.Core;
using System.Reflection;

namespace AgentOS.Tests;

internal static class FeatureIntegrationChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(string root)
    {
        await CheckLegacyMapMigrationAsync(Path.Combine(root, "legacy-map"));
        var project = await PracticeProject.CreateAsync(Path.Combine(root, "journal"));
        await using var runtime = await ProjectRuntime.OpenInternal(project, Path.Combine(root, "feature-state"), new ScriptHost(), Path.Combine(root, "coordination"));
        var state = (ProjectState)typeof(ProjectRuntime).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        var interactions = (TaskInteractionStore)typeof(ProjectRuntime).GetField("_interactions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        var owner = new WorkUnit { Status = WorkStatus.Running };
        var peer = new WorkUnit { Status = WorkStatus.Running };
        state.Work.AddRange([owner, peer]);
        var clarification = interactions.Add(new TaskInteraction { WorkId = owner.Id, Kind = InteractionKind.Clarification, Status = InteractionStatus.Pending, Scope = "selected file", Text = "Which file?" });
        try { await runtime.ReplyClarificationAsync(peer.Id, clarification.Id, "wrong"); throw new Exception("Another task replied to scoped clarification."); }
        catch (InvalidOperationException) { }
        Check((await runtime.ReplyClarificationAsync(owner.Id, clarification.Id, "a.cs")).Response == "a.cs", "Reply was not recorded exactly.");
        try { interactions.Change(clarification.Id, InteractionStatus.Canceled); throw new Exception("Terminal reply was changed."); }
        catch (InvalidOperationException) { }
        var due = interactions.Add(new TaskInteraction { WorkId = owner.Id, Kind = InteractionKind.Wait, Status = InteractionStatus.Pending, Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Check(interactions.Get(due.Id).Status == InteractionStatus.Expired, "Past deadline remained pending.");
        var wait = runtime.CreateWait(owner.Id, WaitKind.Task, peer.Id);
        try { runtime.CreateWait(peer.Id, WaitKind.Task, owner.Id); throw new Exception("Task wait cycle was accepted."); }
        catch (InvalidOperationException) { }
        Check(runtime.CancelWait(owner.Id, wait.Id).Status == InteractionStatus.Canceled, "Canceled wait did not settle.");
        var queued = interactions.Add(new TaskInteraction { WorkId = "missing", Kind = InteractionKind.Steering, Status = InteractionStatus.Queued, Text = "old turn" });
        var obligation = interactions.Add(new TaskInteraction { WorkId = "missing", Kind = InteractionKind.Obligation, Status = InteractionStatus.Pending, Required = true });
        // Reopen the runtime journal at its actual persisted path.
        var journalPath = (string)typeof(TaskInteractionStore).GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(interactions)!;
        var reopened = new TaskInteractionStore(Path.GetDirectoryName(journalPath)!);
        reopened.Recover(id => id == owner.Id || id == peer.Id);
        Check(reopened.Get(queued.Id).Status == InteractionStatus.Rejected, "Orphaned steering replayed after restart.");
        Check(reopened.Get(obligation.Id).Status == InteractionStatus.Pending, "Required obligation was lost on recovery.");
        Check(reopened.Get(clarification.Id).Status == InteractionStatus.Replied, "Completed reply changed on recovery.");
    }

    private static async Task CheckLegacyMapMigrationAsync(string root)
    {
        var project = await PracticeProject.CreateAsync(root);
        await using var runtime = await ProjectRuntime.OpenInternal(project, Path.Combine(root, "feature-state"), new ScriptHost(), Path.Combine(root, "coordination"));
        runtime.Configure("Write-Output passed");
        var node = new MapTask { Title = "Logical task", Prompt = "Write-Output work", Acceptance = "Done", Selected = true };
        var mapId = runtime.SaveDraftMap(new AgentOS.Core.TaskMap { Title = "Attempts", Tasks = [node] });
        var state = (ProjectState)typeof(ProjectRuntime).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        var first = new WorkUnit { ExternalRequestId = node.Id, Relationship = WorkRelationship.Original, Status = WorkStatus.Stale };
        var revision = new WorkUnit { ParentId = first.Id, Relationship = WorkRelationship.Revision, Status = WorkStatus.Completed };
        var followup = new WorkUnit { ParentId = revision.Id, Relationship = WorkRelationship.Followup, Status = WorkStatus.Completed, Task = "Separate followup" };
        state.Work.AddRange([first, revision, followup]);
        typeof(ProjectRuntime).GetMethod("UpdateMapStatuses", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(runtime, null);
        var map = state.Maps.Single(x => x.Id == mapId);
        var mapped = map.Tasks.Single(x => x.Id == node.Id);
        var followupNode = map.Tasks.Single(x => x.WorkIds.Contains(followup.Id));
        Check(mapped.Status == MapTaskStatus.Completed, "Successful revision did not complete its logical map task.");
        Check(mapped.WorkIds.SequenceEqual([first.Id, revision.Id]), "Unrelated followup changed the logical attempt history.");
        Check(followupNode.Id != node.Id, "Separate followup reused the original logical node.");
        Check(followupNode.WorkId == followup.Id, "Separate followup node lost its work ID.");
        Check(followupNode.WorkIds.SequenceEqual([followup.Id]), "Separate followup lost its own work history.");
        Check(map.Edges.Any(x => x.Kind == MapEdgeKind.Followup && x.FromTaskId == node.Id && x.ToTaskId == followupNode.Id), "Separate followup lost its map edge.");
        Check(first.Status == WorkStatus.Stale && revision.Status == WorkStatus.Completed && followup.Status == WorkStatus.Completed, "Updating the map rewrote completed work history.");
    }
}


