using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
namespace AgentOS.Core.Coordination;
public sealed partial class CoordinationService : IDisposable
{
    public const int ProtocolVersion = 1;
    private const int ApplicationId = 0x414F534B;
    private readonly string _root;
    private readonly IntPtr _db;
    private readonly FileStream _ownership;
    private readonly object _gate = new();
    private readonly Func<int,long,bool> _isLive;
    private readonly Func<FaultPoint,bool>? _fault;
    private ExportHealth _export = new(false,"Not exported",0);
    private bool _disposed;
    public string Root => _root;
    public ExportHealth ExportStatus { get { lock(_gate) return _export; } }
    public CoordinationService(string root):this(root,null,null) {}
    internal CoordinationService(string root, Func<int,long,bool>? processLiveness, Func<FaultPoint,bool>? fault)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Explicit root required.",nameof(root));
        _root=Path.GetFullPath(root);
        var oldRoot=Path.GetFullPath(AgentOS.Core.MachineCoordinator.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(_root.Equals(oldRoot,StringComparison.OrdinalIgnoreCase)||_root.StartsWith(oldRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The pre-cutover kernel requires a fresh root outside the user coordinator.");
        Directory.CreateDirectory(_root);
        foreach(var name in new[]{"state.json","journal.json"}) if(File.Exists(Path.Combine(_root,name))) throw new InvalidOperationException($"Legacy {name} exists; select a fresh root. Existing bytes preserved.");
        _ownership=new FileStream(Path.Combine(_root,"coordination.owner"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        _isLive=processLiveness??ProductionLive; _fault=fault;
        var path=Path.Combine(_root,"coordination.sqlite"); var existed=File.Exists(path);
        var rc=sqlite3_open_v2(path,out _db,existed?2:6,IntPtr.Zero);
        if(rc!=0||_db==IntPtr.Zero){if(_db!=IntPtr.Zero) sqlite3_close(_db); _ownership.Dispose(); throw new IOException($"Cannot open coordination database ({rc}); preserve {path}.");}
        try {
            Exec("PRAGMA busy_timeout=5000"); Exec("PRAGMA foreign_keys=ON");
            if(existed){
                if(Scalar("PRAGMA quick_check")!="ok") throw new InvalidDataException("Coordination database integrity check failed; preserve its bytes.");
                if(Scalar("PRAGMA application_id")!=ApplicationId.ToString()||Scalar("PRAGMA user_version")!="1") throw new InvalidDataException("Unsupported coordination database identity or schema; preserve its bytes.");
                if(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='meta'")!="1") throw new InvalidDataException("Incomplete coordination schema; preserve its bytes.");
            } else Initialize();
            if(!string.Equals(Scalar("PRAGMA journal_mode=WAL"),"wal",StringComparison.OrdinalIgnoreCase))throw new IOException("Coordination database refused WAL mode.");
            Exec("PRAGMA synchronous=FULL");
            if(Scalar("PRAGMA foreign_keys")!="1"||Scalar("PRAGMA synchronous")!="2"||Scalar("PRAGMA busy_timeout")!="5000")throw new IOException("Coordination database refused required durability settings.");
            RebuildRegistry();
        } catch(Exception e) {sqlite3_close(_db);_ownership.Dispose();if(existed&&e is IOException)throw new InvalidDataException($"Cannot read coordination database at {path}; preserve its bytes and select a fresh root.",e);throw;}
    }
    private void Initialize(){
        Exec("BEGIN IMMEDIATE");
        try {
            Exec($"PRAGMA application_id={ApplicationId}"); Exec("PRAGMA user_version=1");
            Exec("CREATE TABLE meta(generation INTEGER NOT NULL CHECK(generation>=0), secret TEXT NOT NULL)"); Run("INSERT INTO meta VALUES(0,?)",Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            Exec("CREATE TABLE participants(id TEXT PRIMARY KEY, capability_hash TEXT NOT NULL, pid INTEGER NOT NULL, created_ticks INTEGER NOT NULL, stable_identity TEXT, group_name TEXT, seen_at TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=1))");
            Exec("CREATE TABLE actions(id TEXT PRIMARY KEY, requester TEXT NOT NULL, requester_kind TEXT NOT NULL CHECK(requester_kind IN ('Participant','User','System')), owner TEXT, text TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('open','closed')), revision INTEGER NOT NULL CHECK(revision>=1), created_at TEXT NOT NULL, outcome TEXT CHECK(outcome IN ('Succeeded','Failed','Canceled','Dropped')), explanation TEXT, artifacts TEXT NOT NULL DEFAULT '[]', CHECK((state='open' AND outcome IS NULL AND explanation IS NULL) OR (state='closed' AND outcome IS NOT NULL AND length(trim(explanation))>0)))");
            Exec("CREATE TABLE messages(id TEXT PRIMARY KEY, sender TEXT NOT NULL, recipient TEXT NOT NULL, text TEXT NOT NULL, reply_to TEXT REFERENCES messages(id), created_at TEXT NOT NULL)");
            Exec("CREATE TRIGGER messages_no_update BEFORE UPDATE ON messages BEGIN SELECT RAISE(ABORT,'messages are immutable'); END");
            Exec("CREATE TRIGGER messages_no_delete BEFORE DELETE ON messages BEGIN SELECT RAISE(ABORT,'messages are immutable'); END");
            Exec("CREATE TRIGGER actions_no_reopen BEFORE UPDATE ON actions WHEN OLD.state='closed' BEGIN SELECT RAISE(ABORT,'closed action is final'); END");
            Exec("CREATE TABLE outbox(message_id TEXT NOT NULL REFERENCES messages(id), recipient TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','delivered')), PRIMARY KEY(message_id,recipient))");
            Exec("CREATE TABLE evidence(seq INTEGER PRIMARY KEY AUTOINCREMENT, actor TEXT NOT NULL, command TEXT NOT NULL, object_id TEXT NOT NULL, at TEXT NOT NULL, payload TEXT NOT NULL)");
            Exec("CREATE TRIGGER evidence_no_update BEFORE UPDATE ON evidence BEGIN SELECT RAISE(ABORT,'evidence is append only'); END");
            Exec("CREATE TRIGGER evidence_no_delete BEFORE DELETE ON evidence BEGIN SELECT RAISE(ABORT,'evidence is append only'); END");
            Exec("CREATE TABLE idempotency(actor TEXT NOT NULL, command TEXT NOT NULL, key TEXT NOT NULL, digest TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(actor,key))");
            Exec("COMMIT");
        } catch {Exec("ROLLBACK");throw;}
    }
    private string Command(string actor,string command,string key,object request,Func<string> mutate,Func<bool>? projectionChanged=null){
        if(string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Idempotency key required.",nameof(key));
        var digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        lock(_gate){ EnsureOpen(); Exec("BEGIN IMMEDIATE"); string result;
            try {
                var prior=Rows("SELECT command,digest,result FROM idempotency WHERE actor=? AND key=?",actor,key);
                if(prior.Count>0){if(prior[0][0]!=command||prior[0][1]!=digest) throw new InvalidOperationException("Idempotency key reused with different request content."); result=prior[0][2]!; Exec("COMMIT"); return result;}
                result=mutate();
                Run("INSERT INTO idempotency(actor,command,key,digest,result) VALUES(?,?,?,?,?)",actor,command,key,digest,result);
                if(projectionChanged?.Invoke()!=false)Exec("UPDATE meta SET generation=generation+1");
                if(_fault?.Invoke(FaultPoint.BeforeCommit)==true) throw new IOException("Injected precommit failure.");
                Exec("COMMIT");
            } catch {try{Exec("ROLLBACK");}catch{} throw;}
            RebuildRegistry(); return result;
        }
    }
    private void Event(string actor,string command,string id,object payload)=>Run("INSERT INTO evidence(actor,command,object_id,at,payload) VALUES(?,?,?,?,?)",actor,command,id,Now(),JsonSerializer.Serialize(payload));
    private static string Now()=>DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
    private static string Id()=>Guid.NewGuid().ToString("D");
    private static bool ProductionLive(int pid,long ticks){try{using var p=System.Diagnostics.Process.GetProcessById(pid);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==ticks;}catch(Exception e) when(e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return false;}}
    private void EnsureOpen(){if(_disposed) throw new ObjectDisposedException(nameof(CoordinationService));}
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;sqlite3_close(_db);_ownership.Dispose();}}
    private void Exec(string sql)=>Run(sql);
    private void Run(string sql,params string?[] args){_=Rows(sql,args);}
    private string? Scalar(string sql,params string?[] args)=>Rows(sql,args).FirstOrDefault()?[0];
    private List<string?[]> Rows(string sql,params string?[] args){
        var rc=sqlite3_prepare_v2(_db,sql,-1,out var statement,IntPtr.Zero); if(rc!=0)throw Error(sql);
        try{
            for(var i=0;i<args.Length;i++){
                if(args[i] is null)rc=sqlite3_bind_null(statement,i+1);
                else {var bytes=Encoding.UTF8.GetBytes(args[i]!);var buffer=Marshal.AllocHGlobal(bytes.Length+1);try{Marshal.Copy(bytes,0,buffer,bytes.Length);Marshal.WriteByte(buffer,bytes.Length,0);rc=sqlite3_bind_text(statement,i+1,buffer,bytes.Length,new IntPtr(-1));}finally{Marshal.FreeHGlobal(buffer);}}
                if(rc!=0)throw Error(sql);
            }
            var rows=new List<string?[]>();
            while((rc=sqlite3_step(statement))==100){var cols=sqlite3_column_count(statement);var row=new string?[cols];for(var j=0;j<cols;j++)if(sqlite3_column_type(statement,j)==5)row[j]=null;else{var length=sqlite3_column_bytes(statement,j);var bytes=new byte[length];if(length>0)Marshal.Copy(sqlite3_column_text(statement,j),bytes,0,length);row[j]=Encoding.UTF8.GetString(bytes);}rows.Add(row);}
            if(rc!=101)throw Error(sql);return rows;
        }finally{sqlite3_finalize(statement);}
    }
    private IOException Error(string sql)=>new($"Coordination SQLite operation failed ({sql.Split(' ')[0]}): {Marshal.PtrToStringUTF8(sqlite3_errmsg(_db))}");
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













