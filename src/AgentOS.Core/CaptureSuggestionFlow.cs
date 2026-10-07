namespace AgentOS.Core;

// Separates the included-context lifecycle from continuous draft editing.
public sealed class CaptureSuggestionFlow : IDisposable
{
    CancellationTokenSource? pending;
    public long ContextRevision { get; private set; }
    public long DraftRevision { get; private set; }
    public void EditDraft() => DraftRevision++;
    public long ChangeContext() { ContextRevision++; pending?.Cancel(); return ContextRevision; }
    public bool IsCurrent(long revision) => revision == ContextRevision;
    public void CancelPending() => pending?.Cancel();
    public Task DebounceAsync(Func<CancellationToken, Task> action, TimeSpan delay,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        pending?.Cancel();
        pending?.Dispose();
        var source = new CancellationTokenSource();
        pending = source;
        return RunAsync(source);
        async Task RunAsync(CancellationTokenSource selected)
        {
            try
            {
                await (wait == null ? Task.Delay(delay, selected.Token) : wait(delay, selected.Token));
                if (!selected.IsCancellationRequested) await action(selected.Token);
            }
            catch (OperationCanceledException) when (selected.IsCancellationRequested) { }
            finally { if (ReferenceEquals(pending, selected)) pending = null; selected.Dispose(); }
        }
    }
    public void Dispose() { pending?.Cancel(); pending?.Dispose(); pending = null; }
}

public static class CaptureShortcutPlan
{
    public static TaskMap Create(string project, string suggestion)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(suggestion)) throw new ArgumentException("A project and suggestion are required.");
        var title = suggestion.Length > 100 ? suggestion[..100] : suggestion;
        var map = new TaskMap { ProjectPath = project, Title = title };
        map.Tasks.Add(new MapTask { Title = title, Prompt = suggestion,
            Acceptance = "Complete the requested task and validate the result; report the validation outcome.", Selected = true });
        return map;
    }
    public static TaskMap? Match(TaskMap snapshot, IEnumerable<TaskMap>? maps)
    {
        var found = maps?.SingleOrDefault(m => m.Id == snapshot.Id);
        if (found != null && (found.ProjectPath != snapshot.ProjectPath || found.Tasks.Count != 1 ||
            found.Tasks[0].Id != snapshot.Tasks[0].Id || found.Tasks[0].Prompt != snapshot.Tasks[0].Prompt ||
            !found.ContextRefs.SequenceEqual(snapshot.ContextRefs)))
            throw new InvalidOperationException("Saved shortcut map does not match its launch snapshot.");
        return found;
    }
    public static bool IsStarted(TaskMap map, string taskId)
    {
        var task = map.Tasks.Single(t => t.Id == taskId);
        return task.WorkId is { Length: > 0 } || task.WorkIds.Any(id => !string.IsNullOrWhiteSpace(id));
    }
}




public sealed class CaptureShortcutRegistry<T> where T : class
{
    readonly Dictionary<(long Revision, string Project, string Suggestion), T> entries = new();
    public T GetOrAdd(long revision, string project, string suggestion, Func<T> create)
    {
        var key = (revision, project, suggestion);
        if (!entries.TryGetValue(key, out var value)) entries.Add(key, value = create());
        return value;
    }
    public T? Find(long revision, string project, string suggestion) =>
        entries.TryGetValue((revision, project, suggestion), out var value) ? value : null;
    public static bool RowMatches(T launch, T? displayed, long launchRevision, long currentRevision,
        string launchProject, string currentProject, string launchSuggestion, string displayedSuggestion, bool loading) =>
        !loading && ReferenceEquals(launch, displayed) && launchRevision == currentRevision &&
        string.Equals(launchProject, currentProject, StringComparison.Ordinal) &&
        string.Equals(launchSuggestion, displayedSuggestion, StringComparison.Ordinal);
}
