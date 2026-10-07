using AgentOS.Core;

namespace AgentOS.Tests;

public static class CaptureSuggestionChecks
{
    public static async Task RunAsync()
    {
        using var flow = new CaptureSuggestionFlow();
        var waits = new List<TaskCompletionSource>();
        Task Wait(TimeSpan delay, CancellationToken token)
        {
            if (delay != TimeSpan.FromMilliseconds(600)) throw new Exception("Unexpected debounce delay.");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waits.Add(gate);
            return gate.Task.WaitAsync(token);
        }
        var calls = 0;
        var first = flow.ChangeContext();
        var a = flow.DebounceAsync(_ => { calls++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(600), Wait);
        flow.EditDraft(); flow.EditDraft();
        if (!flow.IsCurrent(first) || flow.DraftRevision != 2) throw new Exception("Draft editing invalidated included context.");
        var second = flow.ChangeContext();
        var b = flow.DebounceAsync(_ => { calls++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(600), Wait);
        waits[0].SetResult(); await a;
        if (calls != 0 || flow.IsCurrent(first) || !flow.IsCurrent(second)) throw new Exception("Stale generation was accepted.");
        waits[1].SetResult(); await b;
        if (calls != 1) throw new Exception("Debounced generation did not run exactly once.");

        var snapshot = CaptureShortcutPlan.Create("C:\\project", "Finish feature and verify tests");
        var task = snapshot.Tasks.Single();
        if (!task.Selected || snapshot.Tasks.Count != 1 || task.Prompt != "Finish feature and verify tests" ||
            !task.Acceptance.Contains("validate", StringComparison.OrdinalIgnoreCase)) throw new Exception("Shortcut plan omitted the exact task or validation.");
        snapshot.ContextRefs.Add(new ContextArtifactRef("artifact-1", ContextArtifactKind.Text, new string('a', 64), "foreground visible text", DateTimeOffset.UtcNow));
        var saved = JsonFormat.Copy(snapshot); saved.Revision = 1;
        if (CaptureShortcutPlan.Match(snapshot, [saved])?.Id != snapshot.Id || CaptureShortcutPlan.IsStarted(saved, task.Id))
            throw new Exception("Uncertain save could not be reconciled by stable identity.");
        saved.Tasks[0].WorkId = "work-1";
        if (!CaptureShortcutPlan.IsStarted(CaptureShortcutPlan.Match(snapshot, [saved])!, task.Id))
            throw new Exception("Uncertain start was not reconciled from persisted WorkId.");
        var wrong = JsonFormat.Copy(saved); wrong.Tasks[0].Prompt = "different";
        try { CaptureShortcutPlan.Match(snapshot, [wrong]); throw new Exception("Mismatched saved plan accepted."); }
        catch (InvalidOperationException) { }
        var wrongRef = JsonFormat.Copy(saved); wrongRef.ContextRefs.Clear();
        try { CaptureShortcutPlan.Match(snapshot, [wrongRef]); throw new Exception("Mismatched accepted context accepted."); }
        catch (InvalidOperationException) { }
        var registry = new CaptureShortcutRegistry<TaskMap>();
        var remembered = registry.GetOrAdd(second, "C:\\project", task.Prompt, () => snapshot);
        var same = registry.GetOrAdd(second, "C:\\project", task.Prompt, () => CaptureShortcutPlan.Create("C:\\project", task.Prompt));
        if (!ReferenceEquals(remembered, same)) throw new Exception("Refresh lost the uncertain launch identity.");
        var replacement = CaptureShortcutPlan.Create("C:\\project", "another suggestion");
        if (CaptureShortcutRegistry<TaskMap>.RowMatches(remembered, replacement, second, second,
            "C:\\project", "C:\\project", task.Prompt, "another suggestion", false) ||
            CaptureShortcutRegistry<TaskMap>.RowMatches(remembered, remembered, second, second,
            "C:\\project", "C:\\project", task.Prompt, task.Prompt, true))
            throw new Exception("Late launch completion could alter a replacement or loading row.");
        if (!CaptureShortcutRegistry<TaskMap>.RowMatches(remembered, same, second, second,
            "C:\\project", "C:\\project", task.Prompt, task.Prompt, false))
            throw new Exception("Stable row could not reconcile its launch.");
        Console.WriteLine("PASS capture suggestion flow and shortcut snapshot checks");
    }
}



