using AgentOS.Core;

namespace AgentOS.App;

// UI command boundary: each action delegates to the real project runtime.
internal sealed class RuntimeUiCommands(ProjectRuntime runtime)
{
    public Task<string> Start(string prompt, bool autoIntegrate, string? parentId = null)
        => runtime.StartAsync(prompt, autoIntegrate, parentId: parentId);
    public Task<TaskInteraction> SendSteering(string id, string text) => runtime.SendSteeringAsync(id, text);
    public Task<string> ReplyAfterCompletion(string id, string text) => runtime.ReplyAfterCompletionAsync(id, text);
    public Task ResumeConflict(string id) => runtime.ResumeConflictAsync(id);
    public Task<string> Revise(string id) => runtime.ReviseAsync(id);
    public Task Integrate(string id) => runtime.IntegrateAsync(id);
    public void Stop(string id) => runtime.Cancel(id);
    public Task Cleanup(string id) => runtime.CleanupAsync(id);
    public Task<HumanDecision> PrepareRelease(string id) => runtime.RequestReleaseAsync(id);
    public Task Decide(string id, bool approve) => runtime.DecideAsync(id, approve);
    public string Transcript(string id) => runtime.TranscriptPath(id);
    public string Diagnostics(string id) => runtime.DiagnosticsPath(id);
}


