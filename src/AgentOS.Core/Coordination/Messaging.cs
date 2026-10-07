using System.Globalization;

namespace AgentOS.Core.Coordination;

// Separate destination addressing and immutable recipient snapshots.
internal sealed class CoordinationMessaging
{
    private readonly CoordinationStore _store;
    private readonly CoordinationIdentity _identity;

    internal CoordinationMessaging(CoordinationStore store, CoordinationIdentity identity)
    {
        _store = store;
        _identity = identity;
    }

    internal static void CreateSchema(CoordinationStore store)
    {
        store.Exec("CREATE TABLE messages(id TEXT PRIMARY KEY, sender TEXT NOT NULL, recipient TEXT NOT NULL, text TEXT NOT NULL, reply_to TEXT REFERENCES messages(id), created_at TEXT NOT NULL)");
        store.Exec("CREATE TRIGGER messages_no_update BEFORE UPDATE ON messages BEGIN SELECT RAISE(ABORT,'messages are immutable'); END");
        store.Exec("CREATE TRIGGER messages_no_delete BEFORE DELETE ON messages BEGIN SELECT RAISE(ABORT,'messages are immutable'); END");
        store.Exec("CREATE TABLE outbox(message_id TEXT NOT NULL REFERENCES messages(id), recipient TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','delivered')), PRIMARY KEY(message_id,recipient))");
    }

    public MessageDetail SendMessage(Actor sender, string to, string text, string key, string? replyTo = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Message text required.");
        var who = _identity.Address(sender);
        var id = _store.Command(who, "send-message", key, new { to, text, replyTo }, tx =>
        {
            _identity.Address(sender);
            var recipients = ResolveRecipients(tx, to);
            if (replyTo is not null && tx.Scalar("SELECT count(*) FROM messages WHERE id=?", replyTo) != "1")
                throw new KeyNotFoundException("Reply target does not exist.");
            var next = CoordinationStore.Id();
            tx.Run("INSERT INTO messages(id,sender,recipient,text,reply_to,created_at) VALUES(?,?,?,?,?,?)",
                next, who, to, text, replyTo, CoordinationStore.Now());
            foreach (var recipient in recipients)
                tx.Run("INSERT INTO outbox(message_id,recipient) VALUES(?,?)", next, recipient);
            tx.Event(who, "send-message", next, new { to, text, replyTo, recipients });
            return next;
        });
        return ReadMessage(id);
    }

    private IReadOnlyList<string> ResolveRecipients(CoordinationTransaction transaction, string to)
    {
        if (to is "user" or "system") return [to];
        if (to == "agents")
            return transaction.Rows("SELECT id FROM participants ORDER BY id").Select(row => row[0]!).ToArray();
        if (Guid.TryParseExact(to, "D", out _) &&
            transaction.Scalar("SELECT count(*) FROM participants WHERE id=?", to) == "1") return [to];
        // The wildcard includes trusted endpoints and registered participants at send time.
        if (to == "*")
            return new[] { "user", "system" }.Concat(
                transaction.Rows("SELECT id FROM participants ORDER BY id").Select(row => row[0]!)).ToArray();
        if (to.StartsWith("group:", StringComparison.Ordinal) && CoordinationIdentity.ValidToken(to[6..]))
            return transaction.Rows("SELECT id FROM participants WHERE group_name=? ORDER BY id", to[6..])
                .Select(row => row[0]!).ToArray();
        throw new ArgumentException("Recipient must be user, system, participant UUID, group:<id>, agents, or *.", nameof(to));
    }

    public MessageDetail ReadMessage(string id)
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            var row = _store.Rows("SELECT id,sender,recipient,text,reply_to,created_at FROM messages WHERE id=?", id)
                .SingleOrDefault() ?? throw new KeyNotFoundException("Message does not exist.");
            var message = new Message(row[0]!, row[1]!, row[2]!, row[3]!, row[4],
                DateTimeOffset.Parse(row[5]!, CultureInfo.InvariantCulture));
            var recipients = _store.Rows("SELECT recipient FROM outbox WHERE message_id=? ORDER BY recipient", id)
                .Select(recipient => recipient[0]!).ToArray();
            return new MessageDetail(message, recipients);
        }
    }

    public IReadOnlyList<(string MessageId, string Recipient, string State)> InspectOutbox()
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            return _store.Rows("SELECT message_id,recipient,state FROM outbox ORDER BY message_id,recipient")
                .Select(row => (row[0]!, row[1]!, row[2]!)).ToArray();
        }
    }
}

