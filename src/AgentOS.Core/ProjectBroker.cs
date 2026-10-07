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
                    WorkUnit? work = null; IReadOnlyList<TaskMap>? maps = null; IReadOnlyList<string>? started = null;
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
                        default: throw new UnauthorizedAccessException("Unsupported project request.");
                    }
                    response = JsonSerializer.Serialize(new BrokerResponse(work, null, maps, started), Wire);
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
internal sealed record BrokerRequest(string Operation, string? Task, string? RequestId, string? WorkId, string? Validation, TaskMap? Map = null, long? ExpectedRevision = null);
internal sealed record BrokerResponse(WorkUnit? Work, string? Error, IReadOnlyList<TaskMap>? Maps = null, IReadOnlyList<string>? Started = null);


