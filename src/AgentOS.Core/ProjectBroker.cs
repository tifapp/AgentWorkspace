using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgentOS.Core;

internal sealed class ProjectBroker : IAsyncDisposable
{
    private readonly ProjectRuntime _runtime;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    internal static readonly JsonSerializerOptions Wire = new(JsonFormat.Options) { WriteIndented = false };
    internal static string Name(string project) => "AgentOS.Project." + StateStore.Key(project);
    public ProjectBroker(ProjectRuntime runtime) { _runtime = runtime; _server = Task.Run(Serve); }
    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Name(_runtime.Snapshot.ProjectPath), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                string response;
                try
                {
                    var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Empty project request.");
                    if (line.Length > 200000) throw new ArgumentException("Project request is too large.");
                    var request = JsonSerializer.Deserialize<BrokerRequest>(line, JsonFormat.Options) ?? throw new IOException("Invalid project request.");
                    WorkUnit? work = null; IReadOnlyList<TaskMap>? maps = null; IReadOnlyList<string>? started = null; IReadOnlyList<TaskInteraction>? interactions = null; TaskInteraction? interaction = null;
                    switch (request.Operation)
                    {
                        case "start":
                            if (request.RequestId == null) throw new ArgumentException("A stable task request identity is required.");
                            if (request.Validation != null && request.Validation.Trim() != _runtime.Snapshot.ValidationCommand)
                                throw new UnauthorizedAccessException("The open project's validation policy differs. Inspect its saved policy; it will not be changed by a task launch.");
                            var id = await _runtime.StartAsync(request.Task ?? "", externalRequestId: request.RequestId);
                            work = _runtime.Snapshot.Work.Single(x => x.Id == id); break;
                        case "work": work = _runtime.Snapshot.Work.Single(x => x.Id == request.WorkId); break;
                        case "cancel": _runtime.Cancel(request.WorkId!); work = _runtime.Snapshot.Work.Single(x => x.Id == request.WorkId); break;
                        case "map-save":
                            if (request.Map == null) throw new ArgumentException("A map is required.");
                            var mapId = _runtime.SaveDraftMap(request.Map, request.ExpectedRevision);
                            maps = [_runtime.Snapshot.Maps.Single(m => m.Id == mapId)]; break;
                        case "map-list": maps = _runtime.Snapshot.Maps; break;
                        case "map-start": started = await _runtime.StartSelectedMapTasksAsync(request.WorkId ?? ""); break;
                        case "inspect-interactions": interactions = _runtime.InspectInteractions(request.WorkId); break;
                        case "steer": interaction = await _runtime.SendSteeringAsync(request.WorkId!, request.Text!); break;
                        case "submit": if (request.RequestId == null) throw new ArgumentException("A stable task request identity is required."); started = [await _runtime.StartAsync(request.Task!, externalRequestId: request.RequestId)]; break;
                        case "reply-after": if (request.RequestId == null) throw new ArgumentException("A stable task request identity is required."); started = [await _runtime.ReplyAfterCompletionAsync(request.WorkId!, request.Text!, request.RequestId)]; break;
                        case "ask-peer": interaction = _runtime.AskPeer(request.WorkId!, request.TargetId!, request.Text!, request.Deadline); break;
                        case "ack-peer": interaction = _runtime.AcknowledgePeer(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "handoff-peer": interaction = _runtime.HandoffPeer(request.WorkId!, request.InteractionId!, request.TargetId!); break;
                        case "resolve-wait": interaction = _runtime.ResolveWait(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "reply": interaction = await _runtime.ReplyClarificationAsync(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "accept-followup": started = [await _runtime.AcceptFollowup(request.WorkId!, request.InteractionId!)]; break;
                        case "resolve": interaction = _runtime.ResolveObligation(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "wait": interaction = _runtime.CreateWait(request.WorkId!, request.WaitKind ?? throw new ArgumentException("Wait kind required."), request.TargetId!, request.Deadline); break;
                        case "cancel-wait": interaction = _runtime.CancelWait(request.WorkId!, request.InteractionId!); break;
                        default: throw new UnauthorizedAccessException("Unsupported project request.");
                    }
                    response = JsonSerializer.Serialize(new BrokerResponse(work, null, maps, started, interactions, interaction), Wire);
                }
                catch (Exception e) { response = JsonSerializer.Serialize(new BrokerResponse(null, e.Message), Wire); }
                await writer.WriteLineAsync(response.AsMemory(), timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _server; _stop.Dispose(); }
}

public static class ProjectClient
{
    public static async Task<WorkUnit?> TryStartAsync(string project, string task, string requestId, string? validation = null, CancellationToken cancel = default)
        => (await Call(project, new("start", task, requestId, null, validation), cancel))?.Work;
    public static async Task<WorkUnit?> InspectAsync(string project, string id, CancellationToken cancel = default)
        => (await Call(project, new("work", null, null, id, null), cancel))?.Work;
    public static async Task<WorkUnit?> CancelAsync(string project, string id, CancellationToken cancel = default)
        => (await Call(project, new("cancel", null, null, id, null), cancel))?.Work;
    public static async Task<IReadOnlyList<TaskMap>?> MapsAsync(string project, CancellationToken cancel = default) => (await Call(project, new("map-list", null, null, null, null), cancel))?.Maps;
    public static async Task<TaskMap?> SaveMapAsync(string project, TaskMap map, long? revision, CancellationToken cancel = default) => (await Call(project, new("map-save", null, null, null, null, map, revision), cancel))?.Maps?.Single();
    public static async Task<IReadOnlyList<string>?> StartMapAsync(string project, string id, CancellationToken cancel = default) => (await Call(project, new("map-start", null, null, id, null), cancel))?.Started;
    public static async Task<IReadOnlyList<TaskInteraction>?> InteractionsAsync(string project, string? workId = null, CancellationToken cancel = default) => (await Call(project, new("inspect-interactions", null, null, workId, null), cancel))?.Interactions;
    public static async Task<string?> SubmitAsync(string project, string task, string requestId, CancellationToken cancel = default) => (await Call(project, new("submit", task, requestId, null, null), cancel))?.Started?.Single();
    public static async Task<string?> ReplyAfterAsync(string project, string workId, string text, string requestId, CancellationToken cancel = default) => (await Call(project, new("reply-after", null, requestId, workId, null, Text: text), cancel))?.Started?.Single();
    public static async Task<TaskInteraction?> AskPeerAsync(string project, string workId, string targetId, string text, DateTimeOffset? deadline = null, CancellationToken cancel = default) => (await Call(project, new("ask-peer", null, null, workId, null, Text: text, TargetId: targetId, Deadline: deadline), cancel))?.Interaction;
    public static async Task<TaskInteraction?> AckPeerAsync(string project, string workId, string requestId, string text, CancellationToken cancel = default) => (await Call(project, new("ack-peer", null, null, workId, null, InteractionId: requestId, Text: text), cancel))?.Interaction;
    public static async Task<TaskInteraction?> HandoffPeerAsync(string project, string workId, string requestId, string targetId, CancellationToken cancel = default) => (await Call(project, new("handoff-peer", null, null, workId, null, InteractionId: requestId, TargetId: targetId), cancel))?.Interaction;
    public static async Task<TaskInteraction?> ResolveWaitAsync(string project, string workId, string waitId, string resolution, CancellationToken cancel = default) => (await Call(project, new("resolve-wait", null, null, workId, null, InteractionId: waitId, Text: resolution), cancel))?.Interaction;
    public static async Task<TaskInteraction?> SteerAsync(string project, string workId, string text, CancellationToken cancel = default) => (await Call(project, new("steer", null, null, workId, null, Text: text), cancel))?.Interaction;
    public static async Task<TaskInteraction?> ReplyAsync(string project, string workId, string requestId, string text, CancellationToken cancel = default) => (await Call(project, new("reply", null, null, workId, null, InteractionId: requestId, Text: text), cancel))?.Interaction;
    public static async Task<string?> AcceptFollowupAsync(string project, string workId, string proposalId, CancellationToken cancel = default) => (await Call(project, new("accept-followup", null, null, workId, null, InteractionId: proposalId), cancel))?.Started?.Single();
    public static async Task<TaskInteraction?> ResolveAsync(string project, string workId, string obligationId, string text, CancellationToken cancel = default) => (await Call(project, new("resolve", null, null, workId, null, InteractionId: obligationId, Text: text), cancel))?.Interaction;
    public static async Task<TaskInteraction?> WaitAsync(string project, string workId, WaitKind kind, string target, DateTimeOffset? deadline = null, CancellationToken cancel = default) => (await Call(project, new("wait", null, null, workId, null, TargetId: target, WaitKind: kind, Deadline: deadline), cancel))?.Interaction;
    public static async Task<TaskInteraction?> CancelWaitAsync(string project, string workId, string waitId, CancellationToken cancel = default) => (await Call(project, new("cancel-wait", null, null, workId, null, InteractionId: waitId), cancel))?.Interaction;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string name, int timeout);
    private static async Task<BrokerResponse?> Call(string project, BrokerRequest request, CancellationToken cancel)
    {
        project = SafePaths.Project((await Commands.Git(SafePaths.Project(project), "rev-parse", "--show-toplevel")).Checked());
        using var pipe = new NamedPipeClientStream(".", ProjectBroker.Name(project), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3500, cancel); }
        catch (TimeoutException)
        {
            var name = @"\\.\pipe\" + ProjectBroker.Name(project);
            if (!WaitNamedPipe(name, 0) && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 2) return null;
            throw new IOException("The project runtime is busy or did not answer. Retry with the same request identity; its state is unknown.");
        }
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProjectBroker.Wire).AsMemory(), cancel);
        var response = JsonSerializer.Deserialize<BrokerResponse>(await reader.ReadLineAsync(cancel) ?? throw new IOException("The task request may have been accepted before its response was lost. Retry with the same request identity."), JsonFormat.Options)
            ?? throw new IOException("Invalid project response.");
        if (response.Error != null) throw new InvalidOperationException(response.Error);
        return response;
    }
}
internal sealed record BrokerRequest(string Operation, string? Task, string? RequestId, string? WorkId, string? Validation, TaskMap? Map = null, long? ExpectedRevision = null, string? InteractionId = null, string? Text = null, string? TargetId = null, WaitKind? WaitKind = null, DateTimeOffset? Deadline = null);
internal sealed record BrokerResponse(WorkUnit? Work, string? Error, IReadOnlyList<TaskMap>? Maps = null, IReadOnlyList<string>? Started = null, IReadOnlyList<TaskInteraction>? Interactions = null, TaskInteraction? Interaction = null);
