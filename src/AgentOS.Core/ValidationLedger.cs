using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOS.Core;

// A runtime-owned shared database, never an agent supplied SQL or database connection.
public sealed class ValidationLedger
{
    private readonly string _file;
    private readonly MachineCoordinator _coordinator;
    public ValidationLedger(string? root = null)
    { var folder = Path.GetFullPath(root ?? MachineCoordinator.DefaultRoot); Directory.CreateDirectory(folder); _file = Path.Combine(folder, "validation.sqlite"); _coordinator = new(folder); }
    public async Task<string> RecordAsync(ValidationEvidence evidence, CancellationToken cancel = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonFormat.Options);
        var id = Convert.ToHexString(SHA256.HashData(bytes));
        using var ownership = await _coordinator.EnterAsync("service:validation-ledger:" + _file, "Record validation evidence", null, cancel);
        var result = sqlite3_open_v2(_file, out var database, 6, IntPtr.Zero);
        if (result != 0) { if (database != IntPtr.Zero) sqlite3_close(database); throw new IOException("The validation database could not be opened: " + result); }
        try
        {
            sqlite3_busy_timeout(database, 5000);
            Execute(database, "CREATE TABLE IF NOT EXISTS evidence(id TEXT PRIMARY KEY, payload BLOB NOT NULL);");
            Execute(database, "BEGIN IMMEDIATE; INSERT OR IGNORE INTO evidence(id,payload) VALUES('" + id + "',X'" + Convert.ToHexString(bytes) + "'); COMMIT;");
            return id;
        }
        finally { sqlite3_close(database); }
    }
    public int Count()
    {
        var result = sqlite3_open_v2(_file, out var database, 1, IntPtr.Zero);
        if (result != 0) { if (database != IntPtr.Zero) sqlite3_close(database); throw new IOException("Validation database is unavailable."); }
        try
        {
            var count = 0;
            Callback callback = (_, _, values, _) => { count = int.Parse(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(values))!); return 0; };
            Execute(database, "SELECT COUNT(*) FROM evidence;", callback); GC.KeepAlive(callback); return count;
        }
        finally { sqlite3_close(database); }
    }
    private static void Execute(IntPtr database, string sql, Callback? callback = null)
    {
        var result = sqlite3_exec(database, sql, callback, IntPtr.Zero, out var error);
        if (result == 0) return;
        var message = error == IntPtr.Zero ? result.ToString() : Marshal.PtrToStringUTF8(error); if (error != IntPtr.Zero) sqlite3_free(error);
        throw new IOException("Validation database operation failed: " + message);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Callback(IntPtr context, int count, IntPtr values, IntPtr names);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, Callback? callback, IntPtr context, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr memory);
}
