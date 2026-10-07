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
                    if (line.Length > 40000) throw new ArgumentException("Project request is too large.");
                    var request = JsonSerializer.Deserialize<BrokerRequest>(line, JsonFormat.Options) ?? throw new IOException("Invalid project request.");
                    WorkUnit? work;
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
                        default: throw new UnauthorizedAccessException("The project broker supports task launch, inspection and cancellation only.");
                    }
                    response = JsonSerializer.Serialize(new BrokerResponse(work, null), Wire);
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
        => await Call(project, new("start", task, requestId, null, validation), cancel);
    public static async Task<WorkUnit?> InspectAsync(string project, string id, CancellationToken cancel = default)
        => await Call(project, new("work", null, null, id, null), cancel);
    public static async Task<WorkUnit?> CancelAsync(string project, string id, CancellationToken cancel = default)
        => await Call(project, new("cancel", null, null, id, null), cancel);
    private static async Task<WorkUnit?> Call(string project, BrokerRequest request, CancellationToken cancel)
    {
        project = SafePaths.Project((await Commands.Git(SafePaths.Project(project), "rev-parse", "--show-toplevel")).Checked());
        using var pipe = new NamedPipeClientStream(".", ProjectBroker.Name(project), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(750, cancel); }
        catch (TimeoutException) { return null; }
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProjectBroker.Wire).AsMemory(), cancel);
        var response = JsonSerializer.Deserialize<BrokerResponse>(await reader.ReadLineAsync(cancel) ?? throw new IOException("The task request may have been accepted before its response was lost. Retry with the same request identity."), JsonFormat.Options)
            ?? throw new IOException("Invalid project response.");
        if (response.Error != null) throw new InvalidOperationException(response.Error);
        return response.Work;
    }
}
internal sealed record BrokerRequest(string Operation, string? Task, string? RequestId, string? WorkId, string? Validation);
internal sealed record BrokerResponse(WorkUnit? Work, string? Error);
