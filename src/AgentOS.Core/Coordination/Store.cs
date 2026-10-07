using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
namespace AgentOS.Core.Coordination;
// Single SQLite owner: connection, durability, exclusion, idempotency, and generation.
internal sealed class CoordinationStore : IDisposable
{
    public const int ProtocolVersion = 1;
    private const int ApplicationId = 0x414F534B;
    private readonly string _root;
    private readonly IntPtr _db;
    private readonly FileStream _ownership;
    private readonly object _gate = new();
    internal object Gate => _gate;
    internal readonly Func<int,long,bool> IsLive;
    internal readonly Func<FaultPoint,bool>? Fault;
    internal System.Action? AfterCommit;
    internal System.Action<Exception>? AfterCommitFailed;
    internal long CommittedGeneration { get; private set; }
    private bool _disposed;
    internal string Root => _root;

    
    internal CoordinationStore(string root, Func<int, long, bool>? processLiveness,
        Func<FaultPoint, bool>? fault)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Explicit root required.", nameof(root));
        _root = Path.GetFullPath(root);
        var oldRoot = Path.GetFullPath(AgentOS.Core.MachineCoordinator.DefaultRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (_root.Equals(oldRoot, StringComparison.OrdinalIgnoreCase) ||
            _root.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pre-cutover kernel requires a fresh root outside the user coordinator.");

        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "state.json", "journal.json" })
            if (File.Exists(Path.Combine(_root, name)))
                throw new InvalidOperationException($"Legacy {name} exists; select a fresh root. Existing bytes preserved.");
        _ownership = new FileStream(Path.Combine(_root, "coordination.owner"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        IsLive = processLiveness ?? ProductionLive;
        Fault = fault;
        var path = Path.Combine(_root, "coordination.sqlite");
        var existed = File.Exists(path);
        var rc = sqlite3_open_v2(path, out _db, existed ? 2 : 6, IntPtr.Zero);
        if (rc != 0 || _db == IntPtr.Zero)
        {
            if (_db != IntPtr.Zero) sqlite3_close(_db);
            _ownership.Dispose();
            throw new IOException($"Cannot open coordination database ({rc}); preserve {path}.");
        }

        try
        {
            Exec("PRAGMA busy_timeout=5000");
            Exec("PRAGMA foreign_keys=ON");
            if (existed)
            {
                if (Scalar("PRAGMA quick_check") != "ok")
                    throw new InvalidDataException("Coordination database integrity check failed; preserve its bytes.");
                if (Scalar("PRAGMA application_id") != ApplicationId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                    Scalar("PRAGMA user_version") != "1")
                    throw new InvalidDataException("Unsupported coordination database identity or schema; preserve its bytes.");
                foreach (var table in new[] { "meta", "participants", "actions", "messages", "outbox", "evidence", "idempotency" })
                    if (Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?", table) != "1")
                        throw new InvalidDataException("Incomplete coordination schema; preserve its bytes.");
            }
            else Initialize();

            if (!string.Equals(Scalar("PRAGMA journal_mode=WAL"), "wal", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Coordination database refused WAL mode.");
            Exec("PRAGMA synchronous=FULL");
            if (Scalar("PRAGMA foreign_keys") != "1" || Scalar("PRAGMA synchronous") != "2" ||
                Scalar("PRAGMA busy_timeout") != "5000")
                throw new IOException("Coordination database refused required durability settings.");
            CommittedGeneration = long.Parse(Scalar("SELECT generation FROM meta")!,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception error)
        {
            sqlite3_close(_db);
            _ownership.Dispose();
            if (existed && error is IOException)
                throw new InvalidDataException($"Cannot read coordination database at {path}; preserve its bytes and select a fresh root.", error);
            throw;
        }
    }

    private void Initialize()
    {
        Exec("BEGIN IMMEDIATE");
        try
        {
            Exec($"PRAGMA application_id={ApplicationId}");
            Exec("PRAGMA user_version=1");
            Exec("CREATE TABLE meta(generation INTEGER NOT NULL CHECK(generation>=0), secret TEXT NOT NULL)");
            Run("INSERT INTO meta VALUES(0,?)", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            CoordinationIdentity.CreateSchema(this);
            CoordinationActions.CreateSchema(this);
            CoordinationMessaging.CreateSchema(this);
            CoordinationEvidence.CreateSchema(this);
            Exec("CREATE TABLE idempotency(actor TEXT NOT NULL, command TEXT NOT NULL, key TEXT NOT NULL, digest TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(actor,key))");
            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
    }
    internal string Command(string actor, string command, string key, object request,
        Func<CoordinationTransaction, string> mutate, Func<bool>? projectionChanged = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key required.", nameof(key));
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

        lock (_gate)
        {
            EnsureOpen();
            Exec("BEGIN IMMEDIATE");
            using var tx = new CoordinationTransaction(this);
            string result;
            long nextGeneration = CommittedGeneration;
            try
            {
                var prior = Rows("SELECT command,digest,result FROM idempotency WHERE actor=? AND key=?", actor, key);
                if (prior.Count > 0)
                {
                    if (prior[0][0] != command || prior[0][1] != digest)
                        throw new InvalidOperationException("Idempotency key reused with different request content.");
                    result = prior[0][2]!;
                    Exec("COMMIT");
                    return result;
                }

                result = mutate(tx);
                Run("INSERT INTO idempotency(actor,command,key,digest,result) VALUES(?,?,?,?,?)",
                    actor, command, key, digest, result);
                if (projectionChanged?.Invoke() != false)
                    Exec("UPDATE meta SET generation=generation+1");
                nextGeneration = long.Parse(Scalar("SELECT generation FROM meta")!,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (Fault?.Invoke(FaultPoint.BeforeCommit) == true)
                    throw new IOException("Injected precommit failure.");
                Exec("COMMIT");
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { }
                throw;
            }

            CommittedGeneration = nextGeneration;
            tx.Close();
            try { AfterCommit?.Invoke(); }
            catch (Exception exportError) { AfterCommitFailed?.Invoke(exportError); }
            return result;
        }
    }
    internal static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        System.Globalization.CultureInfo.InvariantCulture);
    internal static string Id() => Guid.NewGuid().ToString("D");

    private static bool ProductionLive(int pid, long ticks)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UnauthorizedAccessException("Process identity could not be inspected; ownership remains held.");
        }
    }

    internal void EnsureOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CoordinationService));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sqlite3_close(_db);
            _ownership.Dispose();
        }
    }

    internal void Exec(string sql) => Run(sql);
    internal void Run(string sql, params string?[] args) => _ = Rows(sql, args);
    internal string? Scalar(string sql, params string?[] args) => Rows(sql, args).FirstOrDefault()?[0];

    internal List<string?[]> Rows(string sql, params string?[] args)
    {
        lock (_gate)
        {
            EnsureOpen();
            return RowsLocked(sql, args);
        }
    }

    private List<string?[]> RowsLocked(string sql, params string?[] args)
    {
        var rc = sqlite3_prepare_v2(_db, sql, -1, out var statement, IntPtr.Zero);
        if (rc != 0)
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            throw Error(sql);
        }
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is null)
                    rc = sqlite3_bind_null(statement, i + 1);
                else
                {
                    // SQLITE_TRANSIENT copies the exact UTF-8 byte count, including embedded NULs.
                    var bytes = Encoding.UTF8.GetBytes(args[i]!);
                    var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
                    try
                    {
                        Marshal.Copy(bytes, 0, buffer, bytes.Length);
                        Marshal.WriteByte(buffer, bytes.Length, 0);
                        rc = sqlite3_bind_text(statement, i + 1, buffer, bytes.Length, new IntPtr(-1));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                if (rc != 0) throw Error(sql);
            }

            var rows = new List<string?[]>();
            while ((rc = sqlite3_step(statement)) == 100)
            {
                var columns = sqlite3_column_count(statement);
                var row = new string?[columns];
                for (var column = 0; column < columns; column++)
                {
                    if (sqlite3_column_type(statement, column) == 5) continue;
                    var length = sqlite3_column_bytes(statement, column);
                    var bytes = new byte[length];
                    if (length > 0)
                        Marshal.Copy(sqlite3_column_text(statement, column), bytes, 0, length);
                    row[column] = Encoding.UTF8.GetString(bytes);
                }
                rows.Add(row);
            }
            if (rc != 101) throw Error(sql);
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }

    private IOException Error(string sql) =>
        new($"Coordination SQLite operation failed ({sql.Split(' ')[0]}): {Marshal.PtrToStringUTF8(sqlite3_errmsg(_db))}");
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out IntPtr db,int flags,IntPtr vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_prepare_v2(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int length,out IntPtr statement,IntPtr tail);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_bind_text(IntPtr statement,int index,IntPtr value,int length,IntPtr destructor);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_bind_null(IntPtr statement,int index);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_column_type(IntPtr statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr sqlite3_column_text(IntPtr statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_column_bytes(IntPtr statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr sqlite3_errmsg(IntPtr db);
}

// A module receives this scope only during the store's exclusive command transaction.
// It cannot start, commit, or roll back a transaction or open another database.
internal sealed class CoordinationTransaction : IDisposable
{
    private readonly CoordinationStore _store;
    private bool _closed;
    internal CoordinationTransaction(CoordinationStore store) => _store = store;
    internal void Close() => _closed = true;
    public void Dispose() => Close();
    private void Check(string sql)
    {
        if (_closed) throw new ObjectDisposedException(nameof(CoordinationTransaction));
        if (!System.Threading.Monitor.IsEntered(_store.Gate)) throw new InvalidOperationException("Transaction scope requires the store gate.");
        var operation = sql.TrimStart().Split(' ', '\n', '\r', '\t')[0];
        if (operation.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("COMMIT", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Transaction control belongs to the coordination store.");
    }
    internal List<string?[]> Rows(string sql,params string?[] args) { Check(sql); return _store.Rows(sql,args); }
    internal string? Scalar(string sql,params string?[] args) => Rows(sql,args).FirstOrDefault()?[0];
    internal void Run(string sql,params string?[] args) { _ = Rows(sql,args); }
    internal int RunChanges(string sql,params string?[] args)
    {
        Run(sql,args);
        return int.Parse(Scalar("SELECT changes()")!,System.Globalization.CultureInfo.InvariantCulture);
    }
    internal void Event(string actor, string command, string id, object payload) =>
        CoordinationEvidence.Record(this, actor, command, id, payload);
}


