using System.Security.Cryptography;
using System.Text;
namespace AgentOS.Core.Coordination;
public sealed partial class CoordinationService
{
    // These authority objects are for trusted in-process hosts; transport admission is a later layer.
    public Actor TrustedUser()=>new(this,"user",AuthorityKind.User);
    public Actor TrustedSystem()=>new(this,"system",AuthorityKind.System);
    private string Capability(string key,string id){lock(_gate){EnsureOpen();var secret=Convert.FromHexString(Scalar("SELECT secret FROM meta")!);return Convert.ToHexString(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(key+":"+id)));}}
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public ParticipantSession Register(string key,string? stableIdentity=null,string? group=null){
        if(group is not null&&!ValidToken(group))throw new ArgumentException("Invalid group name.",nameof(group));
        using var p=System.Diagnostics.Process.GetCurrentProcess();var pid=p.Id;var ticks=p.StartTime.ToUniversalTime().Ticks;
        var id=Command("service","register",key,new{pid,ticks,stableIdentity,group},()=>{
            var next=Id();var cap=Capability(key,next);
            Run("INSERT INTO participants(id,capability_hash,pid,created_ticks,stable_identity,group_name,seen_at,revision) VALUES(?,?,?,?,?,?,?,1)",next,Hash(cap),pid.ToString(),ticks.ToString(),stableIdentity,group,Now());
            Event("service","register",next,new{pid,ticks,stableIdentity,group});return next;
        });return new(id,Capability(key,id),pid,ticks,stableIdentity,group);
    }
    public Actor Authenticate(ParticipantSession session){lock(_gate){EnsureOpen();var row=Rows("SELECT capability_hash,pid,created_ticks FROM participants WHERE id=?",session.Id).SingleOrDefault();
        if(row is null||!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row[0]!),Convert.FromHexString(Hash(session.Capability)))||!int.TryParse(row[1],out var pid)||!long.TryParse(row[2],out var ticks)||pid!=session.Pid||ticks!=session.ProcessCreatedUtcTicks||!_isLive(pid,ticks))throw new UnauthorizedAccessException("Participant capability or live process identity invalid.");
        return new(this,session.Id,AuthorityKind.Participant,session.Capability);
    }}
    private string Address(Actor actor){lock(_gate){EnsureOpen();if(!ReferenceEquals(actor.Issuer,this))throw new UnauthorizedAccessException("Actor belongs to another coordination service.");
        if(actor.Kind!=AuthorityKind.Participant){if(actor.Capability is not null||actor.Address!=(actor.Kind==AuthorityKind.User?"user":"system"))throw new UnauthorizedAccessException("Invalid trusted authority.");return actor.Address;}
        if(!Guid.TryParseExact(actor.Address,"D",out _))throw new UnauthorizedAccessException("Invalid participant address.");
        var row=Rows("SELECT capability_hash,pid,created_ticks FROM participants WHERE id=?",actor.Address).SingleOrDefault();
        if(row is null||actor.Capability is null||!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row[0]!),Convert.FromHexString(Hash(actor.Capability)))||!_isLive(int.Parse(row[1]!),long.Parse(row[2]!)))throw new UnauthorizedAccessException("Participant is not authenticated and live.");
        return actor.Address;
    }}
    public long Heartbeat(Actor actor,long expectedRevision,string key){var who=Address(actor);if(actor.Kind!=AuthorityKind.Participant)throw new UnauthorizedAccessException("Heartbeat requires participant.");
        var result=Command(who,"heartbeat",key,new{expectedRevision},()=>{Address(actor);var id=who;if(RunChanges("UPDATE participants SET seen_at=?,revision=revision+1 WHERE id=? AND revision=?",Now(),id,expectedRevision.ToString())!=1)throw new InvalidOperationException("Participant revision conflict.");Event(who,"heartbeat",id,new{expectedRevision});return (expectedRevision+1).ToString();});return long.Parse(result);
    }
    private int RunChanges(string sql,params string?[] args){Run(sql,args);return int.Parse(Scalar("SELECT changes()")!);}
    private static bool ValidToken(string value)=>value.Length>0&&value.All(c=>char.IsLetterOrDigit(c)||c is '-' or '_' or '.');
}







