using System.Text.Json.Serialization;

namespace AgentOS.Core.Coordination;

public sealed record Registry(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("entries")] IReadOnlyList<Entry> Entries,
    [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages,
    [property: JsonPropertyName("actions")] IReadOnlyList<Action> Actions);

public sealed record Entry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("holds")] IReadOnlyDictionary<string, string> Holds,
    [property: JsonPropertyName("seenAt")] DateTimeOffset SeenAt);

public sealed record Message(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("replyTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyTo,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record Action(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owner")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Owner,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public enum AuthorityKind { Participant, User, System }
public enum ActionOutcome { Succeeded, Failed, Canceled, Dropped }

/// <summary>Service-issued authority for trusted in-process calls. Future process transports must authenticate peers before issuing one.</summary>
public sealed class Actor
{
    internal CoordinationService Issuer { get; }
    internal string? Capability { get; }
    public string Address { get; }
    public AuthorityKind Kind { get; }

    internal Actor(CoordinationService issuer, string address, AuthorityKind kind, string? capability = null)
    {
        Issuer = issuer;
        Address = address;
        Kind = kind;
        Capability = capability;
    }
}

public sealed record ParticipantSession(string Id, string Capability, int Pid,
    long ProcessCreatedUtcTicks, string? StableIdentity, string? Group);
public sealed record RequesterIdentity(string Address, AuthorityKind Kind);
public sealed record ActionDetail(string Id, RequesterIdentity Requester, string? Owner,
    string Text, string State, long Revision, DateTimeOffset CreatedAt,
    ActionOutcome? Outcome, string? Explanation, IReadOnlyList<string> Artifacts);
public sealed record MessageDetail(Message Message, IReadOnlyList<string> Recipients);
/// <summary>Health of the disposable registry export; database commits remain authoritative.</summary>
public sealed record ExportHealth(bool Healthy, string? Error, long Generation);
internal enum FaultPoint { BeforeCommit, BeforeExport }
