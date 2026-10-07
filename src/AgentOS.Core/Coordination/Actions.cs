using System.Globalization;
using System.Text.Json;

namespace AgentOS.Core.Coordination;

// Typed action lifecycle and assignment policy.
internal sealed class CoordinationActions
{
    private readonly CoordinationStore _store;
    private readonly CoordinationIdentity _identity;

    internal CoordinationActions(CoordinationStore store, CoordinationIdentity identity)
    {
        _store = store;
        _identity = identity;
    }

    internal static void CreateSchema(CoordinationStore store)
    {
        store.Exec("CREATE TABLE actions(id TEXT PRIMARY KEY, requester TEXT NOT NULL, requester_kind TEXT NOT NULL CHECK(requester_kind IN ('Participant','User','System')), owner TEXT, text TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('open','closed')), revision INTEGER NOT NULL CHECK(revision>=1), created_at TEXT NOT NULL, outcome TEXT CHECK(outcome IN ('Succeeded','Failed','Canceled','Dropped')), explanation TEXT, artifacts TEXT NOT NULL DEFAULT '[]', CHECK((state='open' AND outcome IS NULL AND explanation IS NULL) OR (state='closed' AND outcome IS NOT NULL AND length(trim(explanation))>0)))");
        store.Exec("CREATE TRIGGER actions_no_reopen BEFORE UPDATE ON actions WHEN OLD.state='closed' BEGIN SELECT RAISE(ABORT,'closed action is final'); END");
    }

    public ActionDetail CreateAction(Actor requester, string text, string key)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Action text required.");
        var who = _identity.Address(requester);
        var result = _store.Command(who, "create-action", key, new { text }, tx =>
        {
            _identity.Address(requester);
            var id = CoordinationStore.Id();
            tx.Run("INSERT INTO actions(id,requester,requester_kind,text,state,revision,created_at) VALUES(?,?,?,? ,'open',1,?)",
                id, who, requester.Kind.ToString(), text, CoordinationStore.Now());
            tx.Event(who, "create-action", id, new { text });
            return JsonSerializer.Serialize(ReadActionCore(id, tx));
        });
        return JsonSerializer.Deserialize<ActionDetail>(result)!;
    }

    public ActionDetail ReadAction(string id)
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            return ReadActionCore(id, null);
        }
    }
    private ActionDetail ReadActionCore(string id, CoordinationTransaction? transaction)
    {
            var row = (transaction?.Rows("SELECT id,requester,requester_kind,owner,text,state,revision,created_at,outcome,explanation,artifacts FROM actions WHERE id=?", id)
                ?? _store.Rows("SELECT id,requester,requester_kind,owner,text,state,revision,created_at,outcome,explanation,artifacts FROM actions WHERE id=?", id))
                .SingleOrDefault() ?? throw new KeyNotFoundException("Action does not exist.");
            return new ActionDetail(row[0]!,
                new RequesterIdentity(row[1]!, Enum.Parse<AuthorityKind>(row[2]!)),
                row[3], row[4]!, row[5]!,
                long.Parse(row[6]!, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(row[7]!, CultureInfo.InvariantCulture),
                row[8] is null ? null : Enum.Parse<ActionOutcome>(row[8]!),
                row[9], JsonSerializer.Deserialize<string[]>(row[10]!)!);
    }

    private ActionDetail ChangeAction(Actor actor, string id, long expectedRevision, string key,
        string command, string? owner, ActionOutcome? outcome, string? explanation,
        IReadOnlyList<string>? artifacts)
    {
        var who = _identity.Address(actor);
        if (outcome is not null && string.IsNullOrWhiteSpace(explanation))
            throw new ArgumentException("Closure explanation required.");
        var projectionChanged = true;
        var result = _store.Command(who, command, key,
            new { id, expectedRevision, owner, outcome, explanation, artifacts }, tx =>
            {
                _identity.Address(actor);
                var row = tx.Rows("SELECT requester,owner,state,revision FROM actions WHERE id=?", id)
                    .SingleOrDefault() ?? throw new KeyNotFoundException("Action does not exist.");
                if (row[2] != "open") throw new InvalidOperationException("Closed action cannot be changed.");
                if (long.Parse(row[3]!, CultureInfo.InvariantCulture) != expectedRevision)
                    throw new InvalidOperationException("Action revision conflict.");

                var privileged = actor.Kind is AuthorityKind.User or AuthorityKind.System;
                if (command == "claim-action")
                {
                    if (row[1] is not null || actor.Kind != AuthorityKind.Participant || owner != who)
                        throw new UnauthorizedAccessException("Claim requires unassigned action and participant self.");
                }
                else if (command == "release-action")
                {
                    if (row[1] != who || owner is not null)
                        throw new UnauthorizedAccessException("Only owner can release assignment.");
                }
                else if (command == "assign-action")
                {
                    if (!privileged && row[0] != who)
                        throw new UnauthorizedAccessException("Only requester or trusted authority can assign.");
                    if (owner is not null && !ValidAssignee(tx, owner))
                        throw new ArgumentException("Invalid assignee address.");
                }
                else if (command == "close-action")
                {
                    if (!privileged && row[0] != who && row[1] != who)
                        throw new UnauthorizedAccessException("Only requester, owner or trusted authority can close.");
                }

                if (command == "assign-action" && row[1] == owner)
                {
                    projectionChanged = false;
                    return JsonSerializer.Serialize(ReadActionCore(id, tx));
                }

                if (command == "close-action")
                    tx.Run("UPDATE actions SET state='closed',outcome=?,explanation=?,artifacts=?,revision=revision+1 WHERE id=? AND state='open' AND revision=?",
                        outcome!.Value.ToString(), explanation, JsonSerializer.Serialize(artifacts ?? []),
                        id, expectedRevision.ToString(CultureInfo.InvariantCulture));
                else
                    tx.Run("UPDATE actions SET owner=?,revision=revision+1 WHERE id=? AND state='open' AND revision=?",
                        owner, id, expectedRevision.ToString(CultureInfo.InvariantCulture));
                if (tx.Scalar("SELECT changes()") != "1")
                    throw new InvalidOperationException("Action revision conflict.");
                tx.Event(who, command, id, new { expectedRevision, owner, outcome, explanation, artifacts });
                return JsonSerializer.Serialize(ReadActionCore(id, tx));
            }, () => projectionChanged);
        return JsonSerializer.Deserialize<ActionDetail>(result)!;
    }

    private bool ValidAssignee(CoordinationTransaction transaction, string address)
    {
        if (address is "user" or "system") return true;
        if (!Guid.TryParseExact(address, "D", out _)) return false;
        return transaction.Scalar("SELECT count(*) FROM participants WHERE id=?", address) == "1";
    }

    public ActionDetail AssignAction(Actor actor, string id, string? owner, long expectedRevision, string key) =>
        ChangeAction(actor, id, expectedRevision, key, "assign-action", owner, null, null, null);
    public ActionDetail ClaimAction(Actor actor, string id, long expectedRevision, string key) =>
        ChangeAction(actor, id, expectedRevision, key, "claim-action", actor.Address, null, null, null);
    public ActionDetail ReleaseAction(Actor actor, string id, long expectedRevision, string key) =>
        ChangeAction(actor, id, expectedRevision, key, "release-action", null, null, null, null);
    public ActionDetail CloseAction(Actor actor, string id, ActionOutcome outcome, string explanation,
        long expectedRevision, string key, IReadOnlyList<string>? artifacts = null) =>
        ChangeAction(actor, id, expectedRevision, key, "close-action", null, outcome, explanation, artifacts);
}


