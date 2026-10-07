using System.Buffers.Binary;
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
    internal static async Task<string> ReadFrame(Stream stream,CancellationToken cancel)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,cancel);int length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length<1||length>ContextArtifacts.MaxWireBytes)throw new InvalidDataException("Project request exceeds frame limit.");
        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,cancel);return new UTF8Encoding(false,true).GetString(bytes);
    }
    internal static async Task WriteFrame(Stream stream,string message,CancellationToken cancel)
    {
        var bytes=Encoding.UTF8.GetBytes(message);if(bytes.Length<1||bytes.Length>ContextArtifacts.MaxWireBytes)throw new InvalidDataException("Project response exceeds frame limit.");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);await stream.WriteAsync(header,cancel);await stream.WriteAsync(bytes,cancel);await stream.FlushAsync(cancel);
    }    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Name(_runtime.Snapshot.ProjectPath), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                string response;
                try
                {
                    var line = await ReadFrame(pipe,timeout.Token);
                    var request = JsonSerializer.Deserialize<BrokerRequest>(line, JsonFormat.Options) ?? throw new IOException("Invalid project request.");
                    RuntimeProtocol? health = null; UpdateDrain? drain = null; UpdateReadiness? ready = null; ContextArtifactRef? artifact = null;
                    WorkUnit? work = null; IReadOnlyList<TaskMap>? maps = null; IReadOnlyList<string>? started = null; IReadOnlyList<TaskInteraction>? interactions = null; TaskInteraction? interaction = null; IReadOnlyList<ConflictNotice>? conflicts = null; IReadOnlyList<HumanEscalation>? escalations = null;
                    switch (request.Operation)
                    {
                        case "update-health": health = _runtime.UpdateHealth; break;
                        case "update-drain": timeout.CancelAfter(TimeSpan.FromMinutes(2)); drain = await _runtime.BeginUpdateDrainAsync(request.UpdateScope ?? throw new ArgumentException("Update scope required.")); break;
                        case "update-ready": ready = _runtime.UpdateReady(request.UpdateToken ?? ""); break;
                        case "update-exit": _runtime.RequestUpdateExit(request.UpdateToken ?? ""); break;
                        case "update-cancel-map": _runtime.CancelPendingMapForUpdate(request.WorkId ?? throw new ArgumentException("Map id required.")); break;
                        case "start":
                            if (request.RequestId == null) throw new ArgumentException("A stable task request identity is required.");
                            if (request.Validation != null && request.Validation.Trim() != _runtime.Snapshot.ValidationCommand)
                                throw new UnauthorizedAccessException("The open project's validation policy differs. Inspect its saved policy; it will not be changed by a task launch.");
                            var id = await _runtime.StartAsync(request.Task ?? "", externalRequestId: request.RequestId);
                            work = _runtime.Snapshot.Work.Single(x => x.Id == id); break;
                        case "work": work = _runtime.Snapshot.Work.Single(x => x.Id == request.WorkId); break;
                        case "inspect-conflicts":
                            var state = _runtime.Snapshot;
                            conflicts = state.Conflicts.Where(x => request.WorkId == null || x.WorkId == request.WorkId).ToArray();
                            escalations = state.Escalations.Where(x => request.WorkId == null || x.WorkId == request.WorkId).ToArray(); break;
                        case "resume-conflict":
                            _ = _runtime.ResumeConflictAsync(request.WorkId!);
                            work = _runtime.Snapshot.Work.Single(x => x.Id == request.WorkId); break;
                        case "cancel": _runtime.Cancel(request.WorkId!); work = _runtime.Snapshot.Work.Single(x => x.Id == request.WorkId); break;
                        case "context-save": if(request.ArtifactBytes==null||request.ArtifactKind==null||request.Source==null)throw new ArgumentException("Context bytes and source required.");artifact=new ContextArtifacts(_runtime.DataDirectory).Accept(request.ArtifactKind.Value,request.ArtifactBytes,request.Source,request.CapturedAt??DateTimeOffset.UtcNow);break;
                        case "map-save":
                            if (request.Map == null) throw new ArgumentException("A map is required.");
                            var mapId = _runtime.SaveDraftMap(request.Map, request.ExpectedRevision);
                            maps = [_runtime.Snapshot.Maps.Single(m => m.Id == mapId)]; break;
                        case "map-list": maps = _runtime.Snapshot.Maps; break;
                        case "map-start": started = request.SelectedTaskIds==null?await _runtime.StartSelectedMapTasksAsync(request.WorkId??""):await _runtime.StartSelectedMapTasksAsync(request.WorkId??"",request.SelectedTaskIds,request.ExpectedRevision??throw new ArgumentException("Expected map revision required."));break;
                        case "inspect-interactions": interactions = _runtime.InspectInteractions(request.WorkId); break;
                        case "steer": interaction = await _runtime.SendSteeringAsync(request.WorkId!, request.Text!); break;
                        case "submit": if (request.RequestId == null) throw new ArgumentException("A stable task request identity is required."); started = [await _runtime.StartAsync(request.Task!, externalRequestId: request.RequestId)]; break;
                        case "reply-after": if (request.RequestId == null) throw new ArgumentException("A stable task request identity is required."); started = [await _runtime.ReplyAfterCompletionAsync(request.WorkId!, request.Text!, request.RequestId)]; break;
                        case "ask-peer": interaction = _runtime.AskPeer(request.WorkId!, request.TargetId!, request.Text!, request.Deadline); break;
                        case "ack-peer": interaction = _runtime.AcknowledgePeer(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "handoff-peer": interaction = _runtime.HandoffPeer(request.WorkId!, request.InteractionId!, request.TargetId!); break;
                        case "resolve-wait": interaction = _runtime.ResolveWait(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "reply": interaction = await _runtime.ReplyClarificationAsync(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "reject-followup": interaction=_runtime.RejectFollowup(request.WorkId!,request.InteractionId!,request.Text!);break;
                        case "accept-followup": started = [await _runtime.AcceptFollowup(request.WorkId!, request.InteractionId!)]; break;
                        case "resolve": interaction = _runtime.ResolveObligation(request.WorkId!, request.InteractionId!, request.Text!); break;
                        case "wait": interaction = _runtime.CreateWait(request.WorkId!, request.WaitKind ?? throw new ArgumentException("Wait kind required."), request.TargetId!, request.Deadline); break;
                        case "cancel-wait": interaction = _runtime.CancelWait(request.WorkId!, request.InteractionId!); break;
                        default: throw new UnauthorizedAccessException("Unsupported project request.");
                    }
                    response = JsonSerializer.Serialize(new BrokerResponse(work, null, maps, started, interactions, interaction, conflicts, escalations, health, drain, ready, artifact), Wire);
                }
                catch (Exception e) { response = JsonSerializer.Serialize(new BrokerResponse(null, e.Message), Wire); }
                await WriteFrame(pipe,response,timeout.Token);
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
    public static async Task<ConflictInspection?> ConflictsAsync(string project, string? workId = null, CancellationToken cancel = default)
    {
        var response = await Call(project, new("inspect-conflicts", null, null, workId, null), cancel);
        return response == null ? null : new ConflictInspection(response.Conflicts ?? [], response.Escalations ?? []);
    }
    public static async Task<WorkUnit?> ResumeConflictAsync(string project, string workId, CancellationToken cancel = default)
        => (await Call(project, new("resume-conflict", null, null, workId, null), cancel))?.Work;
    public static async Task<WorkUnit?> CancelAsync(string project, string id, CancellationToken cancel = default)
        => (await Call(project, new("cancel", null, null, id, null), cancel))?.Work;
    public static async Task<ContextArtifactRef> SaveContextAsync(string project,ContextArtifactKind kind,byte[] bytes,string source,DateTimeOffset capturedAt,CancellationToken cancel=default)
    {
        if(bytes==null||bytes.Length<1||bytes.Length>(kind==ContextArtifactKind.Text?ContextArtifacts.MaxTextBytes:ContextArtifacts.MaxImageBytes))throw new ArgumentException("Context exceeds its size limit.");
        return (await Call(project,new("context-save",null,null,null,null,ArtifactKind:kind,ArtifactBytes:bytes,Source:source,CapturedAt:capturedAt),cancel))?.Artifact??throw new IOException("Project runtime did not return a context reference.");
    }    public static async Task<IReadOnlyList<TaskMap>?> MapsAsync(string project, CancellationToken cancel = default) => (await Call(project, new("map-list", null, null, null, null), cancel))?.Maps;
    public static async Task<TaskMap?> SaveMapAsync(string project, TaskMap map, long? revision, CancellationToken cancel = default) => (await Call(project, new("map-save", null, null, null, null, map, revision), cancel))?.Maps?.Single();
    public static async Task<IReadOnlyList<string>?> StartMapAsync(string project, string id, CancellationToken cancel = default) => (await Call(project, new("map-start", null, null, id, null), cancel))?.Started;
    public static async Task<IReadOnlyList<string>?> StartMapAsync(string project,string id,IReadOnlyList<string> selectedTaskIds,long expectedRevision,CancellationToken cancel=default)=>(await Call(project,new("map-start",null,null,id,null,ExpectedRevision:expectedRevision,SelectedTaskIds:selectedTaskIds),cancel))?.Started;
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
    public static async Task<TaskInteraction?> RejectFollowupAsync(string project,string workId,string proposalId,string reason,CancellationToken cancel=default)=>(await Call(project,new("reject-followup",null,null,workId,null,InteractionId:proposalId,Text:reason),cancel))?.Interaction;
    public static async Task<TaskInteraction?> ResolveAsync(string project, string workId, string obligationId, string text, CancellationToken cancel = default) => (await Call(project, new("resolve", null, null, workId, null, InteractionId: obligationId, Text: text), cancel))?.Interaction;
    public static async Task<TaskInteraction?> WaitAsync(string project, string workId, WaitKind kind, string target, DateTimeOffset? deadline = null, CancellationToken cancel = default) => (await Call(project, new("wait", null, null, workId, null, TargetId: target, WaitKind: kind, Deadline: deadline), cancel))?.Interaction;
    public static async Task<TaskInteraction?> CancelWaitAsync(string project, string workId, string waitId, CancellationToken cancel = default) => (await Call(project, new("cancel-wait", null, null, workId, null, InteractionId: waitId), cancel))?.Interaction;
    public static async Task<RuntimeProtocol?> UpdateHealthAsync(string project) => (await Call(project, new("update-health", null, null, null, null), default))?.Health;
    public static async Task<UpdateDrain?> BeginUpdateDrainAsync(string project, UpdateScope scope) => (await Call(project, new("update-drain", null, null, null, null, UpdateScope: scope), default))?.Drain;
    public static async Task<UpdateReadiness?> UpdateReadyAsync(string project, string token) => (await Call(project, new("update-ready", null, null, null, null, UpdateToken: token), default))?.Ready;
    public static async Task<bool> UpdateExitAsync(string project, string token) => (await Call(project, new("update-exit", null, null, null, null, UpdateToken: token), default)) != null;
    public static async Task<bool> CancelPendingMapForUpdateAsync(string project,string mapId) => (await Call(project,new("update-cancel-map",null,null,mapId,null),default)) != null;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string name, int timeout);
    private static async Task<BrokerResponse?> Call(string project, BrokerRequest request, CancellationToken cancel)
    {
        project = (await GitProjectProfile.Inspect(project)).SourcePath;
        using var pipe = new NamedPipeClientStream(".", ProjectBroker.Name(project), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3500, cancel); }
        catch (TimeoutException)
        {
            var name = @"\\.\pipe\" + ProjectBroker.Name(project);
            if (!WaitNamedPipe(name, 0) && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 2) return null;
            throw new IOException("The project runtime is busy or did not answer. Retry with the same request identity; its state is unknown.");
        }
        await ProjectBroker.WriteFrame(pipe,JsonSerializer.Serialize(request,ProjectBroker.Wire),cancel);
        var response = JsonSerializer.Deserialize<BrokerResponse>(await ProjectBroker.ReadFrame(pipe,cancel),JsonFormat.Options)
            ?? throw new IOException("Invalid project response.");
        if (response.Error != null) throw new InvalidOperationException(response.Error);
        return response;
    }
}
internal sealed record BrokerRequest(string Operation, string? Task, string? RequestId, string? WorkId, string? Validation, TaskMap? Map = null, long? ExpectedRevision = null, string? InteractionId = null, string? Text = null, string? TargetId = null, WaitKind? WaitKind = null, DateTimeOffset? Deadline = null, UpdateScope? UpdateScope = null, string? UpdateToken = null, IReadOnlyList<string>? SelectedTaskIds = null, ContextArtifactKind? ArtifactKind = null, byte[]? ArtifactBytes = null, string? Source = null, DateTimeOffset? CapturedAt = null);
public sealed record ConflictInspection(IReadOnlyList<ConflictNotice> Conflicts, IReadOnlyList<HumanEscalation> Escalations);
internal sealed record BrokerResponse(WorkUnit? Work, string? Error, IReadOnlyList<TaskMap>? Maps = null, IReadOnlyList<string>? Started = null, IReadOnlyList<TaskInteraction>? Interactions = null, TaskInteraction? Interaction = null, IReadOnlyList<ConflictNotice>? Conflicts = null, IReadOnlyList<HumanEscalation>? Escalations = null, RuntimeProtocol? Health = null, UpdateDrain? Drain = null, UpdateReadiness? Ready = null, ContextArtifactRef? Artifact = null);

