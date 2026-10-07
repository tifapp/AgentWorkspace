using System.Text.Json;
using System.Text.Json.Serialization;
namespace AgentOS.Core.Coordination;

// Disposable registry projection. SQLite evidence remains the authoritative record.
internal sealed class CoordinationPresentation
{
    private readonly CoordinationStore _store;
    private ExportHealth _export = new(false, "Not exported", 0);
    private static readonly JsonSerializerOptions RegistryJson = new()
    {
        WriteIndented = false,
        Converters = { new UtcZConverter() }
    };

    internal CoordinationPresentation(CoordinationStore store) => _store = store;

    internal ExportHealth ExportStatus
    {
        get { lock (_store.Gate) return _export; }
    }

    public Registry Snapshot()
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            return SnapshotCore();
        }
    }

    private Registry SnapshotCore()
    {
        var generation = long.Parse(_store.Scalar("SELECT generation FROM meta")!,
            System.Globalization.CultureInfo.InvariantCulture);
        var entries = _store.Rows("SELECT id,seen_at FROM participants ORDER BY id")
            .Select(row => new Entry(row[0]!, CoordinationAdmission.ProjectHolds(_store, row[0]!),
                DateTimeOffset.Parse(row[1]!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var messages = _store.Rows("SELECT id,sender,recipient,text,reply_to,created_at FROM messages ORDER BY created_at,id")
            .Select(row => new Message(row[0]!, row[1]!, row[2]!, row[3]!, row[4],
                DateTimeOffset.Parse(row[5]!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var actions = _store.Rows("SELECT id,owner,text,state,created_at FROM actions ORDER BY created_at,id")
            .Select(row => new Action(row[0]!, row[1], row[2]!, row[3]!,
                DateTimeOffset.Parse(row[4]!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return new Registry(1, generation, entries, messages, actions);
    }
    internal void RecordFailure(Exception error)
    {
        lock (_store.Gate)
        {
            _export = new ExportHealth(false, error.Message, _store.CommittedGeneration);
        }
    }

    public ExportHealth RebuildRegistry()
    {
        lock (_store.Gate)
        {
            _store.EnsureOpen();
            var destination = Path.Combine(_store.Root, "registry.json");
            var temp = Path.Combine(_store.Root, "registry." + Guid.NewGuid().ToString("N") + ".tmp");
            Exception? failure = null;
            long generation = _store.CommittedGeneration;

            try
            {
                var snapshot = SnapshotCore();
                generation = snapshot.Generation;
                if (_store.Fault?.Invoke(FaultPoint.BeforeExport) == true)
                    throw new IOException("Injected postcommit export failure.");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, RegistryJson);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                File.Move(temp, destination, true);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception cleanupError)
                {
                    failure ??= cleanupError;
                }
            }

            _export = failure is null
                ? new ExportHealth(true, null, generation)
                : new ExportHealth(false, failure.Message, _store.CommittedGeneration);
            return _export;
        }
    }
    private sealed class UtcZConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                System.Globalization.CultureInfo.InvariantCulture));
    }
}

