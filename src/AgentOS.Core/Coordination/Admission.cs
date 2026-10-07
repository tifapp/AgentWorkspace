using System.Globalization;
using System.Text.Json;

namespace AgentOS.Core.Coordination;

public sealed record ResourceRequest(string Resource, string Mode);
public sealed record AdmissionDetail(
    string Id, string Owner, string State, long Revision,
    IReadOnlyList<ResourceRequest> Resources, string? Reason, DateTimeOffset CreatedAt);

internal sealed class CoordinationAdmission
{
    private readonly CoordinationStore store;
    private readonly CoordinationIdentity identity;
    private readonly IResourceAdapter resourceAdapter;

    internal CoordinationAdmission(CoordinationStore store, CoordinationIdentity identity, IResourceAdapter resourceAdapter)
    {
        this.store = store;
        this.identity = identity;
        this.resourceAdapter = resourceAdapter;
    }

    internal static void CreateSchema(CoordinationStore store)
    {
        store.Exec("CREATE TABLE admissions(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,owner TEXT NOT NULL REFERENCES participants(id),state TEXT NOT NULL CHECK(state IN ('pending','admitted','suspended','released','canceled')),revision INTEGER NOT NULL CHECK(revision>=1),reason TEXT,created_at TEXT NOT NULL)");
        store.Exec("CREATE TABLE admission_resources(admission_id TEXT NOT NULL REFERENCES admissions(id),resource TEXT NOT NULL,mode TEXT NOT NULL CHECK(mode IN ('read','write')),path TEXT,identity TEXT,is_directory INTEGER NOT NULL,PRIMARY KEY(admission_id,resource))");
        store.Exec("CREATE INDEX admissions_state_seq ON admissions(state,seq)");
    }

    private sealed record Bundle(string Id, string Owner, string State, List<CanonicalResource> Claims);

    public AdmissionDetail Request(Actor actor, IReadOnlyList<ResourceRequest> resources, string key)
    {
        if (actor.Kind != AuthorityKind.Participant)
            throw new UnauthorizedAccessException("Participant owner required.");
        if (resources is null || resources.Count == 0)
            throw new ArgumentException("Nonempty bundle required.");

        var who = identity.Address(actor);
        var requested = resources.ToArray();
        var changed = false;
        var result = store.Command(who, "resource-request", key, requested, tx =>
        {
            identity.Address(actor);
            var claims = requested.Select(resourceAdapter.Resolve)
                .OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
            if (claims.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != claims.Count)
                throw new ArgumentException("Duplicate resources or conversion.");
            foreach (var claim in claims)
                resourceAdapter.Revalidate(claim);
            if (Load(tx, "admitted", "pending", "suspended").Where(b => b.Owner == who)
                .Any(b => b.Claims.Any(c => claims.Any(n => resourceAdapter.Overlaps(c, n)))))
                throw new InvalidOperationException("Release overlapping owned bundle before conversion.");

            var id = CoordinationStore.Id();
            tx.Run("INSERT INTO admissions(id,owner,state,revision,created_at) VALUES(?,?,'pending',1,?)",
                id, who, CoordinationStore.Now());
            foreach (var claim in claims)
                tx.Run("INSERT INTO admission_resources VALUES(?,?,?,?,?,?)",
                    id, claim.Key, claim.Mode, claim.Path, claim.Identity, claim.Directory ? "1" : "0");

            if (!Load(tx, "admitted", "pending", "suspended").Where(b => b.Id != id)
                .Any(b => b.Claims.Any(c => claims.Any(n => Conflict(c, n)))))
            {
                tx.Run("UPDATE admissions SET state='admitted' WHERE id=?", id);
                changed = true;
                tx.Event(who, "resource-admitted", id,
                    new { resources = claims.Select(c => new { c.Key, c.Mode }) });
            }
            else
            {
                tx.Event(who, "resource-pending", id,
                    new { resources = claims.Select(c => new { c.Key, c.Mode }) });
            }
            return JsonSerializer.Serialize(ReadCore(tx, id));
        }, () => changed);
        return JsonSerializer.Deserialize<AdmissionDetail>(result)!;
    }

    public AdmissionDetail Finish(Actor actor, string id, long expectedRevision, string key, bool cancel)
    {
        var who = identity.Address(actor);
        var changed = false;
        var result = store.Command(who, cancel ? "resource-cancel" : "resource-release",
            key, new { id, expectedRevision }, tx =>
        {
            identity.Address(actor);
            var item = ReadCore(tx, id);
            if (item.Owner != who)
                throw new UnauthorizedAccessException("Only bundle owner can finish admission.");
            if (item.Revision != expectedRevision)
                throw new InvalidOperationException("Admission revision conflict.");
            if (cancel && item.State is not ("pending" or "suspended"))
                throw new InvalidOperationException("Only pending work can be canceled.");
            if (!cancel && item.State != "admitted")
                throw new InvalidOperationException("Only admitted work can be released.");

            changed = !cancel;
            tx.Run("UPDATE admissions SET state=?,revision=revision+1,reason=NULL WHERE id=?",
                cancel ? "canceled" : "released", id);
            tx.Event(who, cancel ? "resource-canceled" : "resource-released",
                id, new { expectedRevision });
            Promote(tx, ref changed);
            return JsonSerializer.Serialize(ReadCore(tx, id));
        }, () => changed);
        return JsonSerializer.Deserialize<AdmissionDetail>(result)!;
    }
    private void Promote(CoordinationTransaction tx, ref bool changed)
    {
        var blockers = Load(tx, "admitted");
        foreach (var next in Load(tx, "pending", "suspended"))
        {
            if (next.State == "suspended" ||
                blockers.Any(b => b.Claims.Any(c => next.Claims.Any(n => Conflict(c, n)))))
            {
                blockers.Add(next);
                continue;
            }

            try
            {
                RevalidateOwner(tx, next.Owner);
                foreach (var claim in next.Claims)
                    resourceAdapter.Revalidate(claim);
            }
            catch (Exception error) when (error is ResourceValidationException or UnauthorizedAccessException)
            {
                tx.Run("UPDATE admissions SET state='suspended',revision=revision+1,reason=? WHERE id=?",
                    error.Message, next.Id);
                tx.Event(next.Owner, "resource-suspended", next.Id, new { reason = error.Message });
                blockers.Add(next);
                continue;
            }

            tx.Run("UPDATE admissions SET state='admitted',revision=revision+1 WHERE id=?", next.Id);
            tx.Event(next.Owner, "resource-admitted", next.Id, new { promoted = true });
            changed = true;
            blockers.Add(next);
        }
    }

    private void RevalidateOwner(CoordinationTransaction tx, string owner)
    {
        var row = tx.Rows("SELECT pid,created_ticks FROM participants WHERE id=?", owner)
            .SingleOrDefault() ?? throw new UnauthorizedAccessException("Owner missing.");
        if (!store.IsLive(
            int.Parse(row[0]!, CultureInfo.InvariantCulture),
            long.Parse(row[1]!, CultureInfo.InvariantCulture)))
            throw new UnauthorizedAccessException("Owner process dead or unknown.");
    }

    public AdmissionDetail Read(Actor actor, string id)
    {
        var who = identity.Address(actor);
        lock (store.Gate)
        {
            var item = ReadCore(null, id);
            if (actor.Kind == AuthorityKind.Participant && item.Owner != who)
                throw new UnauthorizedAccessException("Admission belongs to another actor.");
            return item;
        }
    }

    public IReadOnlyList<AdmissionDetail> List(Actor actor)
    {
        var who = identity.Address(actor);
        lock (store.Gate)
        {
            var rows = actor.Kind == AuthorityKind.Participant
                ? store.Rows("SELECT id FROM admissions WHERE owner=? ORDER BY seq", who)
                : store.Rows("SELECT id FROM admissions ORDER BY seq");
            return rows.Select(row => ReadCore(null, row[0]!)).ToArray();
        }
    }

    private AdmissionDetail ReadCore(CoordinationTransaction? tx, string id)
    {
        var row = (tx?.Rows("SELECT owner,state,revision,reason,created_at FROM admissions WHERE id=?", id)
            ?? store.Rows("SELECT owner,state,revision,reason,created_at FROM admissions WHERE id=?", id))
            .SingleOrDefault() ?? throw new KeyNotFoundException("Admission missing.");
        var claims = (tx?.Rows("SELECT resource,mode FROM admission_resources WHERE admission_id=? ORDER BY resource", id)
            ?? store.Rows("SELECT resource,mode FROM admission_resources WHERE admission_id=? ORDER BY resource", id))
            .Select(r => new ResourceRequest(r[0]!, r[1]!)).ToArray();
        return new AdmissionDetail(id, row[0]!, row[1]!,
            long.Parse(row[2]!, CultureInfo.InvariantCulture), claims, row[3],
            DateTimeOffset.Parse(row[4]!, CultureInfo.InvariantCulture));
    }

    internal static IReadOnlyDictionary<string, string> ProjectHolds(CoordinationStore store, string owner)
    {
        var holds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in store.Rows(
            "SELECT r.resource,r.mode FROM admissions a JOIN admission_resources r ON r.admission_id=a.id WHERE a.owner=? AND a.state='admitted' ORDER BY a.seq", owner))
            holds[row[0]!] = row[1]!;
        return holds;
    }

    private static List<Bundle> Load(CoordinationTransaction tx, params string[] states)
    {
        return tx.Rows("SELECT id,owner,state FROM admissions ORDER BY seq")
            .Where(row => states.Contains(row[2]!))
            .Select(row => new Bundle(row[0]!, row[1]!, row[2]!,
                tx.Rows("SELECT resource,mode,path,identity,is_directory FROM admission_resources WHERE admission_id=?", row[0])
                    .Select(claim => new CanonicalResource(
                        claim[0]!, claim[1]!, claim[2], claim[3], claim[4] == "1"))
                    .ToList()))
            .ToList();
    }

    private bool Conflict(CanonicalResource left, CanonicalResource right) =>
        (left.Mode == "write" || right.Mode == "write") && resourceAdapter.Overlaps(left, right);
}
