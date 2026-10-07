namespace AgentOS.Core.Coordination;
public sealed partial class CoordinationService
{
    public MessageDetail SendMessage(Actor sender,string to,string text,string key,string? replyTo=null){
        if(string.IsNullOrWhiteSpace(text))throw new ArgumentException("Message text required.");var who=Address(sender);
        var id=Command(who,"send-message",key,new{to,text,replyTo},()=>{Address(sender);
            var recipients=ResolveRecipients(to);
            if(replyTo is not null&&Scalar("SELECT count(*) FROM messages WHERE id=?",replyTo)!="1")throw new KeyNotFoundException("Reply target does not exist.");
            var next=Id();Run("INSERT INTO messages(id,sender,recipient,text,reply_to,created_at) VALUES(?,?,?,?,?,?)",next,who,to,text,replyTo,Now());
            foreach(var recipient in recipients)Run("INSERT INTO outbox(message_id,recipient) VALUES(?,?)",next,recipient);
            Event(who,"send-message",next,new{to,text,replyTo,recipients});return next;
        });return ReadMessage(id);
    }
    private IReadOnlyList<string> ResolveRecipients(string to){
        if(to is "user" or "system")return [to];
        if(to=="agents")return Rows("SELECT id FROM participants ORDER BY id").Select(x=>x[0]!).ToArray();
        if(Guid.TryParseExact(to,"D",out _)&&Scalar("SELECT count(*) FROM participants WHERE id=?",to)=="1")return [to];
        // Wildcard snapshots both trusted endpoints and every registered participant at send time.
        if(to=="*")return new[]{"user","system"}.Concat(Rows("SELECT id FROM participants ORDER BY id").Select(x=>x[0]!)).ToArray();
        if(to.StartsWith("group:",StringComparison.Ordinal)&&ValidToken(to[6..]))return Rows("SELECT id FROM participants WHERE group_name=? ORDER BY id",to[6..]).Select(x=>x[0]!).ToArray();
        throw new ArgumentException("Recipient must be user, system, participant UUID, group:<id>, agents, or *.",nameof(to));
    }
    public MessageDetail ReadMessage(string id){lock(_gate){EnsureOpen();var r=Rows("SELECT id,sender,recipient,text,reply_to,created_at FROM messages WHERE id=?",id).SingleOrDefault()??throw new KeyNotFoundException("Message does not exist.");
        var message=new Message(r[0]!,r[1]!,r[2]!,r[3]!,r[4],DateTimeOffset.Parse(r[5]!));var recipients=Rows("SELECT recipient FROM outbox WHERE message_id=? ORDER BY recipient",id).Select(x=>x[0]!).ToArray();return new(message,recipients);
    }}
    public IReadOnlyList<(string MessageId,string Recipient,string State)> InspectOutbox(){lock(_gate){EnsureOpen();return Rows("SELECT message_id,recipient,state FROM outbox ORDER BY message_id,recipient").Select(r=>(r[0]!,r[1]!,r[2]!)).ToArray();}}
}




