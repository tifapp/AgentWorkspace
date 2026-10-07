using System.Text.Json;
using System.Text.Json.Serialization;
namespace AgentOS.Core.Coordination;
public sealed partial class CoordinationService
{
    private static readonly JsonSerializerOptions RegistryJson=new(){WriteIndented=false,Converters={new UtcZConverter()}};
    public long EvidenceCount { get { lock(_gate) { EnsureOpen(); return long.Parse(Scalar("SELECT count(*) FROM evidence")!); } } }
    public Registry Snapshot(){lock(_gate){EnsureOpen();return SnapshotCore();}}
    private Registry SnapshotCore(){
        var generation=long.Parse(Scalar("SELECT generation FROM meta")!);
        var entries=Rows("SELECT id,seen_at FROM participants ORDER BY id").Select(r=>new Entry(r[0]!,new Dictionary<string,string>(),DateTimeOffset.Parse(r[1]!))).ToArray();
        var messages=Rows("SELECT id,sender,recipient,text,reply_to,created_at FROM messages ORDER BY created_at,id").Select(r=>new Message(r[0]!,r[1]!,r[2]!,r[3]!,r[4],DateTimeOffset.Parse(r[5]!))).ToArray();
        var actions=Rows("SELECT id,owner,text,state,created_at FROM actions ORDER BY created_at,id").Select(r=>new Action(r[0]!,r[1],r[2]!,r[3]!,DateTimeOffset.Parse(r[4]!))).ToArray();
        return new Registry(1,generation,entries,messages,actions);
    }
    public ExportHealth RebuildRegistry(){lock(_gate){EnsureOpen();var snapshot=SnapshotCore();var destination=Path.Combine(_root,"registry.json");var temp=Path.Combine(_root,"registry."+Guid.NewGuid().ToString("N")+".tmp");
        try{
            if(_fault?.Invoke(FaultPoint.BeforeExport)==true)throw new IOException("Injected postcommit export failure.");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(snapshot,RegistryJson);
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}
            File.Move(temp,destination,true);
            _export=new(true,null,snapshot.Generation);
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException){_export=new(false,e.Message,snapshot.Generation);}
        finally{try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}}
        return _export;
    }}
    private sealed class UtcZConverter:JsonConverter<DateTimeOffset>{
        public override DateTimeOffset Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>DateTimeOffset.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer,DateTimeOffset value,JsonSerializerOptions options)=>writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
    }
}

