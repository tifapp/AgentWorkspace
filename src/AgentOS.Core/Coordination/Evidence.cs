using System.Globalization;
using System.Text.Json;

namespace AgentOS.Core.Coordination;

// Append-only command evidence schema and count.
internal sealed class CoordinationEvidence
{
    private readonly CoordinationStore _store;

    internal CoordinationEvidence(CoordinationStore store) => _store = store;

    internal static void CreateSchema(CoordinationStore store)
    {
        store.Exec("CREATE TABLE evidence(seq INTEGER PRIMARY KEY AUTOINCREMENT, actor TEXT NOT NULL, command TEXT NOT NULL, object_id TEXT NOT NULL, at TEXT NOT NULL, payload TEXT NOT NULL)");
        store.Exec("CREATE TRIGGER evidence_no_update BEFORE UPDATE ON evidence BEGIN SELECT RAISE(ABORT,'evidence is append only'); END");
        store.Exec("CREATE TRIGGER evidence_no_delete BEFORE DELETE ON evidence BEGIN SELECT RAISE(ABORT,'evidence is append only'); END");
    }

    internal static void Record(CoordinationTransaction transaction, string actor,
        string command, string id, object payload)
    {
        transaction.Run("INSERT INTO evidence(actor,command,object_id,at,payload) VALUES(?,?,?,?,?)",
            actor, command, id, CoordinationStore.Now(), JsonSerializer.Serialize(payload));
    }

    public long EvidenceCount
    {
        get
        {
            lock (_store.Gate)
            {
                _store.EnsureOpen();
                return long.Parse(_store.Scalar("SELECT count(*) FROM evidence")!, CultureInfo.InvariantCulture);
            }
        }
    }
}
