using System.Text.Json;
namespace AgentOS.Core.Coordination;
public sealed partial class CoordinationService
{
    public ActionDetail CreateAction(Actor requester,string text,string key){if(string.IsNullOrWhiteSpace(text))throw new ArgumentException("Action text required.");var who=Address(requester);
        var id=Command(who,"create-action",key,new{text},()=>{Address(requester);var next=Id();Run("INSERT INTO actions(id,requester,requester_kind,text,state,revision,created_at) VALUES(?,?,?,? ,'open',1,?)",next,who,requester.Kind.ToString(),text,Now());Event(who,"create-action",next,new{text});return JsonSerializer.Serialize(ReadAction(next));});return JsonSerializer.Deserialize<ActionDetail>(id)!;
    }
    public ActionDetail ReadAction(string id){lock(_gate){EnsureOpen();var r=Rows("SELECT id,requester,owner,text,state,revision,created_at,outcome,explanation,artifacts FROM actions WHERE id=?",id).SingleOrDefault()??throw new KeyNotFoundException("Action does not exist.");return new(r[0]!,new RequesterIdentity(r[1]!,Enum.Parse<AuthorityKind>(Scalar("SELECT requester_kind FROM actions WHERE id=?",id)!)),r[2],r[3]!,r[4]!,long.Parse(r[5]!),DateTimeOffset.Parse(r[6]!),r[7] is null?null:Enum.Parse<ActionOutcome>(r[7]!),r[8],JsonSerializer.Deserialize<string[]>(r[9]!)!);}}
    private ActionDetail ChangeAction(Actor actor,string id,long expectedRevision,string key,string command,string? owner,ActionOutcome? outcome,string? explanation,IReadOnlyList<string>? artifacts){
        var who=Address(actor);if(outcome is not null&&string.IsNullOrWhiteSpace(explanation))throw new ArgumentException("Closure explanation required.");
        var projectionChanged=true;
        var result=Command(who,command,key,new{id,expectedRevision,owner,outcome,explanation,artifacts},()=>{
            Address(actor);
            var row=Rows("SELECT requester,owner,state,revision FROM actions WHERE id=?",id).SingleOrDefault()??throw new KeyNotFoundException("Action does not exist.");
            if(row[2]!="open")throw new InvalidOperationException("Closed action cannot be changed.");
            if(long.Parse(row[3]!)!=expectedRevision)throw new InvalidOperationException("Action revision conflict.");
            var privileged=actor.Kind is AuthorityKind.User or AuthorityKind.System;
            if(command=="claim-action") {if(row[1] is not null||actor.Kind!=AuthorityKind.Participant||owner!=who)throw new UnauthorizedAccessException("Claim requires unassigned action and participant self.");}
            else if(command=="release-action") {if(row[1]!=who||owner is not null)throw new UnauthorizedAccessException("Only owner can release assignment.");}
            else if(command=="assign-action") {if(!privileged&&row[0]!=who)throw new UnauthorizedAccessException("Only requester or trusted authority can assign.");if(owner is not null&&!ValidAssignee(owner))throw new ArgumentException("Invalid assignee address.");}
            else if(command=="close-action") {if(!privileged&&row[0]!=who&&row[1]!=who)throw new UnauthorizedAccessException("Only requester, owner or trusted authority can close.");}
            if(command=="assign-action"&&row[1]==owner){projectionChanged=false;return JsonSerializer.Serialize(ReadAction(id));}
            if(command=="close-action")Run("UPDATE actions SET state='closed',outcome=?,explanation=?,artifacts=?,revision=revision+1 WHERE id=? AND state='open' AND revision=?",outcome!.Value.ToString(),explanation,JsonSerializer.Serialize(artifacts??[]),id,expectedRevision.ToString());
            else Run("UPDATE actions SET owner=?,revision=revision+1 WHERE id=? AND state='open' AND revision=?",owner,id,expectedRevision.ToString());
            if(Scalar("SELECT changes()")!="1")throw new InvalidOperationException("Action revision conflict.");Event(who,command,id,new{expectedRevision,owner,outcome,explanation,artifacts});return JsonSerializer.Serialize(ReadAction(id));
        },()=>projectionChanged);return JsonSerializer.Deserialize<ActionDetail>(result)!;
    }
    private bool ValidAssignee(string address){if(address is "user" or "system")return true;if(!Guid.TryParseExact(address,"D",out _))return false;return Scalar("SELECT count(*) FROM participants WHERE id=?",address)=="1";}
    public ActionDetail AssignAction(Actor actor,string id,string? owner,long expectedRevision,string key)=>ChangeAction(actor,id,expectedRevision,key,"assign-action",owner,null,null,null);
    public ActionDetail ClaimAction(Actor actor,string id,long expectedRevision,string key)=>ChangeAction(actor,id,expectedRevision,key,"claim-action",actor.Address,null,null,null);
    public ActionDetail ReleaseAction(Actor actor,string id,long expectedRevision,string key)=>ChangeAction(actor,id,expectedRevision,key,"release-action",null,null,null,null);
    public ActionDetail CloseAction(Actor actor,string id,ActionOutcome outcome,string explanation,long expectedRevision,string key,IReadOnlyList<string>? artifacts=null)=>ChangeAction(actor,id,expectedRevision,key,"close-action",null,outcome,explanation,artifacts);
}





