using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentOS.Core.Coordination;

// Participant schema, capability validation, and service-bound actor issuance.
internal sealed class CoordinationIdentity
{
    private readonly CoordinationStore _store;
    private readonly CoordinationService _issuer;

    internal CoordinationIdentity(CoordinationStore store, CoordinationService issuer)
    {
        _store = store;
        _issuer = issuer;
    }

    internal static void CreateSchema(CoordinationStore store)
    {
        store.Exec("CREATE TABLE participants(id TEXT PRIMARY KEY, capability_hash TEXT NOT NULL, pid INTEGER NOT NULL, created_ticks INTEGER NOT NULL, stable_identity TEXT, group_name TEXT, seen_at TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=1))");
    }

    // Only a trusted in-process host can call these authority factories.
    public Actor TrustedUser() => new(_issuer, "user", AuthorityKind.User);
    public Actor TrustedSystem() => new(_issuer, "system", AuthorityKind.System);

    private string Capability(string key, string id, CoordinationTransaction? transaction = null)
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            var secret = Convert.FromHexString((transaction?.Scalar("SELECT secret FROM meta") ?? _store.Scalar("SELECT secret FROM meta"))!);
            return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(key + ":" + id)));
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public ParticipantSession Register(string key, string? stableIdentity = null, string? group = null)
    {
        if (group is not null && !ValidToken(group))
            throw new ArgumentException("Invalid group name.", nameof(group));

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var pid = process.Id;
        var ticks = process.StartTime.ToUniversalTime().Ticks;
        var id = _store.Command("service", "register", key, new { pid, ticks, stableIdentity, group }, tx =>
        {
            var next = CoordinationStore.Id();
            var capability = Capability(key, next, tx);
            tx.Run("INSERT INTO participants(id,capability_hash,pid,created_ticks,stable_identity,group_name,seen_at,revision) VALUES(?,?,?,?,?,?,?,1)",
                next, Hash(capability), pid.ToString(CultureInfo.InvariantCulture),
                ticks.ToString(CultureInfo.InvariantCulture), stableIdentity, group, CoordinationStore.Now());
            tx.Event("service", "register", next, new { pid, ticks, stableIdentity, group });
            return next;
        });
        return new ParticipantSession(id, Capability(key, id), pid, ticks, stableIdentity, group);
    }

    public Actor Authenticate(ParticipantSession session)
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            var row = _store.Rows("SELECT capability_hash,pid,created_ticks FROM participants WHERE id=?", session.Id).SingleOrDefault();
            if (row is null ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row[0]!), Convert.FromHexString(Hash(session.Capability))) ||
                !int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
                !long.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                pid != session.Pid || ticks != session.ProcessCreatedUtcTicks || !_store.IsLive(pid, ticks))
                throw new UnauthorizedAccessException("Participant capability or live process identity invalid.");
            return new Actor(_issuer, session.Id, AuthorityKind.Participant, session.Capability);
        }
    }

    internal string Address(Actor actor)
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            if (!ReferenceEquals(actor.Issuer, _issuer))
                throw new UnauthorizedAccessException("Actor belongs to another coordination service.");

            if (actor.Kind != AuthorityKind.Participant)
            {
                if (actor.Capability is not null || actor.Address != (actor.Kind == AuthorityKind.User ? "user" : "system"))
                    throw new UnauthorizedAccessException("Invalid trusted authority.");
                return actor.Address;
            }

            if (!Guid.TryParseExact(actor.Address, "D", out _))
                throw new UnauthorizedAccessException("Invalid participant address.");
            var row = _store.Rows("SELECT capability_hash,pid,created_ticks FROM participants WHERE id=?", actor.Address).SingleOrDefault();
            if (row is null || actor.Capability is null ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row[0]!), Convert.FromHexString(Hash(actor.Capability))) ||
                !_store.IsLive(int.Parse(row[1]!, CultureInfo.InvariantCulture), long.Parse(row[2]!, CultureInfo.InvariantCulture)))
                throw new UnauthorizedAccessException("Participant is not authenticated and live.");
            return actor.Address;
        }
    }

    public long Heartbeat(Actor actor, long expectedRevision, string key)
    {
        var who = Address(actor);
        if (actor.Kind != AuthorityKind.Participant)
            throw new UnauthorizedAccessException("Heartbeat requires participant.");

        var result = _store.Command(who, "heartbeat", key, new { expectedRevision }, tx =>
        {
            Address(actor);
            if (tx.RunChanges("UPDATE participants SET seen_at=?,revision=revision+1 WHERE id=? AND revision=?",
                    CoordinationStore.Now(), who, expectedRevision.ToString(CultureInfo.InvariantCulture)) != 1)
                throw new InvalidOperationException("Participant revision conflict.");
            tx.Event(who, "heartbeat", who, new { expectedRevision });
            return (expectedRevision + 1).ToString(CultureInfo.InvariantCulture);
        });
        return long.Parse(result, CultureInfo.InvariantCulture);
    }

    internal static bool ValidToken(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}

