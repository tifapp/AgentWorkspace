using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOS.Core;

public enum WorkStatus { Preparing, Running, Private, Waiting, Validating, Completed, Stale, Failed, Canceled, Unknown }
public enum DecisionStatus { Pending, Approved, Rejected, Completed, Stale, Unknown }

public sealed class ProjectState
{
    public int Schema { get; set; } = 2;
    public long Generation { get; set; }
    public string ProjectPath { get; set; } = "";
    public string ProjectName => Path.GetFileName(ProjectPath);
    public string IntegratedCommit { get; set; } = "";
    public string ValidationCommand { get; set; } = "";
    public string CodexPath { get; set; } = "";
    public List<WorkUnit> Work { get; set; } = [];
    public List<TaskMap> Maps { get; set; } = [];
    public List<HumanDecision> Decisions { get; set; } = [];
    public List<EffectEvent> Events { get; set; } = [];
}

public sealed class WorkUnit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Task { get; set; } = "";
    public string? Title { get; set; }
    public string? CodexReport { get; set; }
    public string? ParentId { get; set; }
    public string? ExternalRequestId { get; set; }
    public WorkStatus Status { get; set; } = WorkStatus.Preparing;
    public string Detail { get; set; } = "Preparing a private workspace.";
    public string Workspace { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string? CandidateCommit { get; set; }
    public string? IntegratedCommit { get; set; }
    public string? PendingCommit { get; set; }
    public string? PendingBase { get; set; }
    public string? ThreadId { get; set; }
    public string? HostVersion { get; set; }
    public string? HostModel { get; set; }
    public string ValidationCommand { get; set; } = "";
    public bool AutoIntegrate { get; set; } = true;
    public bool WorkspaceRemoved { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> ChangedPaths { get; set; } = [];
    public string Diff { get; set; } = "";
    public List<ValidationEvidence> Evidence { get; set; } = [];
    public string StatusLabel => Status == WorkStatus.Private ? "Private candidate" : Status.ToString();
    public string ShortTask => (Title ?? Task).Length <= 110 ? Title ?? Task : (Title ?? Task)[..107] + "…";
    public bool IsActive => Status is WorkStatus.Preparing or WorkStatus.Running or WorkStatus.Waiting or WorkStatus.Validating;
}

public sealed class ValidationEvidence
{
    public string Commit { get; set; } = "";
    public string Tree { get; set; } = "";
    public string AgainstCommit { get; set; } = "";
    public string Command { get; set; } = "";
    public string Environment { get; set; } = "";
    public string EnvironmentSha256 { get; set; } = "";
    public string RuntimeSha256 { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string LogSha256 { get; set; } = "";
    public int ExitCode { get; set; }
    public bool SourceUnchanged { get; set; }
    public DateTimeOffset TestedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Passed => ExitCode == 0 && SourceUnchanged;
}

public sealed class HumanDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WorkId { get; set; } = "";
    public string Candidate { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Explanation { get; set; } = "";
    public DecisionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
}

public sealed record EffectEvent(DateTimeOffset At, string? WorkId, string Kind, string Message);
public sealed record Prerequisite(string Name, bool Ready, string Detail);
public sealed record HostResult(int ExitCode, bool TurnCompleted, string? ThreadId, string? Report = null, string? Model = null);

public static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

