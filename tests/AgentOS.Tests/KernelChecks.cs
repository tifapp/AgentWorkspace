using AgentOS.Core.Coordination;
using System.Text.Json;
using System.Runtime.InteropServices;
namespace AgentOS.Tests;
internal static class KernelChecks
{
    private static string Fresh(string root,string name){var path=Path.Combine(root,"kernel-"+name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Refuses<T>(System.Action work) where T:Exception {var rejected=false;try{work();}catch(T){rejected=true;}if(!rejected)throw new Exception("Expected "+typeof(T).Name);}
    public static async Task RunAsync(Func<string,Func<Task>,Task> test,string root)
    {
        await test("Kernel exact registry schema and no Git prerequisite",()=>{
            var path=Fresh(root,"schema");using var service=new CoordinationService(path);
            var user=service.TrustedUser();var action=service.CreateAction(user,"Review request","action-1");var message=service.SendMessage(user,"system","hello","message-1");
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"registry.json")));var r=doc.RootElement;
            Check(r.EnumerateObject().Select(x=>x.Name).SequenceEqual(new[]{"version","generation","entries","messages","actions"}),"Unexpected registry keys.");
            Check(r.GetProperty("version").GetInt32()==1&&r.GetProperty("generation").GetInt64()==2,"Wrong version or generation.");
            Check(!Directory.Exists(Path.Combine(path,".git")),"Test unexpectedly needed Git.");
            Check(r.GetProperty("entries").GetArrayLength()==0,"Unexpected entry.");
            var m=r.GetProperty("messages")[0];Check(m.EnumerateObject().Select(x=>x.Name).SequenceEqual(new[]{"id","from","to","text","createdAt"}),"Message schema leaked details.");
            Check(m.GetProperty("createdAt").GetString()!.EndsWith("Z"),"Timestamp is not UTC Z.");
            var a=r.GetProperty("actions")[0];Check(a.EnumerateObject().Select(x=>x.Name).SequenceEqual(new[]{"id","text","state","createdAt"}),"Action schema leaked details.");
            Check(Guid.TryParse(action.Id,out _)&&Guid.TryParse(message.Message.Id,out _),"Object ids are not UUIDs.");return Task.CompletedTask;
        });
        await test("Kernel text roundtrips Unicode, quotes and embedded NUL",()=>{
            var path=Fresh(root,"text");var payload="O'Brien caf� \uD83D\uDE80"+'\0'+"after";
            using(var service=new CoordinationService(path)){var user=service.TrustedUser();var action=service.CreateAction(user,payload,"action");var message=service.SendMessage(user,"system",payload,"message");Check(action.Text==payload&&message.Message.Text==payload,"Immediate text roundtrip changed bytes.");}
            using(var service=new CoordinationService(path)){Check(service.Snapshot().Actions.Single().Text==payload&&service.Snapshot().Messages.Single().Text==payload,"Restart text roundtrip changed bytes.");}
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"registry.json")));Check(doc.RootElement.GetProperty("messages")[0].GetProperty("text").GetString()==payload,"Projection text roundtrip changed bytes.");return Task.CompletedTask;
        });
        await test("Kernel registration, authentication and spoof rejection",()=>{
            var path=Fresh(root,"identity");ParticipantSession session;
            using(var service=new CoordinationService(path)){
                session=service.Register("registration", "stable-1", "team");var again=service.Register("registration","stable-1","team");
                Check(session==again,"Registration retry changed incarnation or capability.");var actor=service.Authenticate(session);Check(actor.Address==session.Id,"Wrong address.");
                Refuses<UnauthorizedAccessException>(()=>service.Authenticate(session with {Capability="bad"}));
                Refuses<UnauthorizedAccessException>(()=>service.Authenticate(session with {ProcessCreatedUtcTicks=0}));
                using(var other=new CoordinationService(Fresh(root,"other-service"))){Refuses<UnauthorizedAccessException>(()=>other.CreateAction(actor,"spoof","key"));}
                var first=service.Snapshot().Generation;Check(service.Heartbeat(actor,1,"beat")==2&&service.Snapshot().Generation==first+1,"Heartbeat revision or projection failed.");
            }
            var database=Path.Combine(path,"coordination.sqlite");
            Check(File.Exists(database),"Committed coordination database missing.");
            foreach(var file in new[]{database,database+"-wal"}.Where(File.Exists))
                Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(session.Capability),"Plaintext capability stored in database or WAL.");
            return Task.CompletedTask;
        });
        await test("Kernel action assignment race, revisions and idempotency",async()=>{
            using var service=new CoordinationService(Fresh(root,"actions"));var user=service.TrustedUser();var a=service.Authenticate(service.Register("a"));var b=service.Authenticate(service.Register("b"));
            var task=service.CreateAction(user,"Do work","create");var before=service.Snapshot().Generation;
            Check(service.CreateAction(user,"Do work","create").Id==task.Id&&service.Snapshot().Generation==before,"Replay changed generation.");
            Refuses<InvalidOperationException>(()=>service.CreateAction(user,"changed","create"));
            Refuses<InvalidOperationException>(()=>service.SendMessage(user,"system","cross command","create"));
            var attempts=await Task.WhenAll(Task.Run(()=>TryClaim(a)),Task.Run(()=>TryClaim(b)));
            Check(attempts.Count(x=>x)==1,"Concurrent claims did not have one winner.");
            var owned=service.ReadAction(task.Id);Check(owned.Revision==2&&owned.Owner is not null,"Claim was not atomic.");
            Check(service.CreateAction(user,"Do work","create").Revision==1,"Idempotent action response changed after claim.");
            Refuses<InvalidOperationException>(()=>service.AssignAction(user,task.Id,a.Address,1,"stale"));
            var loser=owned.Owner==a.Address?b:a;Refuses<UnauthorizedAccessException>(()=>service.ReleaseAction(loser,task.Id,2,"wrong-release"));
            var released=service.ReleaseAction(owned.Owner==a.Address?a:b,task.Id,2,"release");Check(released.Owner is null&&released.Revision==3,"Release failed.");
            var sameGeneration=service.Snapshot().Generation;Check(service.AssignAction(user,task.Id,null,3,"same-owner").Revision==3&&service.Snapshot().Generation==sameGeneration,"No-op assignment advanced generation.");
            bool TryClaim(Actor actor){try{service.ClaimAction(actor,task.Id,1,"claim");return true;}catch(InvalidOperationException){return false;}catch(UnauthorizedAccessException){return false;}}
        });
        await test("Kernel closure outcomes and authority",()=>{
            foreach(var outcome in Enum.GetValues<ActionOutcome>()){
                using var service=new CoordinationService(Fresh(root,"outcome"));var user=service.TrustedUser();var owner=service.Authenticate(service.Register("owner"));var unrelated=service.Authenticate(service.Register("unrelated"));
                var action=service.CreateAction(user,"Task","create");var assigned=service.AssignAction(user,action.Id,owner.Address,1,"assign");
                Refuses<UnauthorizedAccessException>(()=>service.CloseAction(unrelated,action.Id,outcome,"wrong",2,"deny"));
                Refuses<ArgumentException>(()=>service.CloseAction(owner,action.Id,outcome," ",2,"blank"));
                var closed=service.CloseAction(owner,action.Id,outcome,"Exact explanation",2,"close",["artifact://proof"]);
                Check(closed.State=="closed"&&closed.Outcome==outcome&&closed.Explanation=="Exact explanation"&&closed.Artifacts.Single()=="artifact://proof"&&closed.Owner==owner.Address,"Closure lost typed outcome or owner.");
                Refuses<InvalidOperationException>(()=>service.AssignAction(user,action.Id,null,3,"reopen"));
                Check(service.CloseAction(owner,action.Id,outcome,"Exact explanation",2,"close",["artifact://proof"]).Revision==3,"Closure retry changed revision.");
                Check(assigned.CreatedAt==closed.CreatedAt,"Creation timestamp changed.");
                Check(service.CreateAction(user,"Task","create").State=="open"&&service.AssignAction(user,action.Id,owner.Address,1,"assign").Revision==2,"Earlier command replay did not retain committed response.");
            }return Task.CompletedTask;
        });
        await test("Kernel message snapshots, outbox and duplicate send",()=>{
            using var service=new CoordinationService(Fresh(root,"messages"));var a=service.Authenticate(service.Register("a",group:"team"));var b=service.Authenticate(service.Register("b",group:"team"));
            var first=service.SendMessage(a,"group:team","hello","send");Check(first.Recipients.Count==2&&first.Recipients.Contains(a.Address)&&first.Recipients.Contains(b.Address),"Group snapshot wrong.");
            service.Register("c",group:"team");Check(service.ReadMessage(first.Message.Id).Recipients.Count==2,"Send-time snapshot changed.");
            var generation=service.Snapshot().Generation;Check(service.SendMessage(a,"group:team","hello","send").Message.Id==first.Message.Id&&service.Snapshot().Generation==generation,"Duplicate send changed effect.");
            var reply=service.SendMessage(b,a.Address,"reply","reply",first.Message.Id);Check(reply.Message.ReplyTo==first.Message.Id&&reply.Recipients.SequenceEqual(new[]{a.Address}),"Direct reply snapshot wrong.");
            var agents=service.SendMessage(a,"agents","broadcast","agents");Check(agents.Recipients.Count==3&&!agents.Recipients.Contains("user"),"Agents snapshot wrong.");
            var all=service.SendMessage(a,"*","all","all");Check(all.Recipients.Count==5&&all.Recipients.Contains("user")&&all.Recipients.Contains("system"),"Wildcard snapshot wrong.");
            Refuses<KeyNotFoundException>(()=>service.SendMessage(a,"system","bad reply","bad",Guid.NewGuid().ToString("D")));
            Refuses<ArgumentException>(()=>service.SendMessage(a,"not-a-uuid","bad target","invalid"));
            Check(service.InspectOutbox().Count==11,"Outbox missing or duplicated recipient rows.");return Task.CompletedTask;
        });
        await test("Kernel rollback and postcommit export recovery",()=>{
            var rollbackRoot=Fresh(root,"rollback");var failCommit=true;
            using(var service=new CoordinationService(rollbackRoot,null,point=>point==FaultPoint.BeforeCommit&&failCommit)){
                var user=service.TrustedUser();Refuses<IOException>(()=>service.CreateAction(user,"one","retry"));
                Check(service.Snapshot().Generation==0&&service.Snapshot().Actions.Count==0&&service.EvidenceCount==0,"Failed transaction left state.");
                failCommit=false;Check(service.CreateAction(user,"one","retry").Revision==1,"Failed key was retained.");
            }
            var exportRoot=Fresh(root,"export");var failExport=false;string id;
            using(var service=new CoordinationService(exportRoot,null,point=>point==FaultPoint.BeforeExport&&failExport)){
                var user=service.TrustedUser();failExport=true;id=service.CreateAction(user,"committed","key").Id;
                Check(!service.ExportStatus.Healthy&&service.Snapshot().Actions.Single().Id==id,"Postcommit export failure obscured authority.");
                var generation=service.Snapshot().Generation;Check(service.CreateAction(user,"committed","key").Id==id&&service.Snapshot().Generation==generation,"Retry duplicated committed command.");
                failExport=false;Check(service.RebuildRegistry().Healthy,"Export rebuild failed.");
            }
            File.WriteAllText(Path.Combine(exportRoot,"registry.json"),"malformed");
            using(var reopened=new CoordinationService(exportRoot)){Check(reopened.Snapshot().Actions.Single().Id==id&&reopened.ExportStatus.Healthy,"Startup did not restore export from DB.");using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(exportRoot,"registry.json")));Check(doc.RootElement.GetProperty("generation").GetInt64()==1,"Recovered generation wrong.");}
            return Task.CompletedTask;
        });
        await test("Kernel rejects legacy, unsupported and corrupt roots without import",()=>{
            foreach(var name in new[]{"state.json","journal.json"}){var path=Fresh(root,"legacy");var file=Path.Combine(path,name);File.WriteAllText(file,"old bytes");Refuses<InvalidOperationException>(()=>new CoordinationService(path));Check(File.ReadAllText(file)=="old bytes"&&!File.Exists(Path.Combine(path,"coordination.sqlite")),"Legacy state changed.");}
            var bad=Fresh(root,"corrupt");var db=Path.Combine(bad,"coordination.sqlite");File.WriteAllText(db,"not sqlite");Refuses<Exception>(()=>new CoordinationService(bad));Check(File.ReadAllText(db)=="not sqlite","Corrupt DB changed.");
            foreach(var pragma in new[]{"user_version=1","user_version=99","application_id=123"}){
                var unsupported=Fresh(root,"unsupported");using(var initialized=new CoordinationService(unsupported)){}
                var versionDb=Path.Combine(unsupported,"coordination.sqlite");SetPragma(versionDb,pragma);var before=File.ReadAllBytes(versionDb);
                Refuses<InvalidDataException>(()=>new CoordinationService(unsupported));
                Check(File.ReadAllBytes(versionDb).SequenceEqual(before),"Unsupported database bytes changed.");
            }
            return Task.CompletedTask;
        });
        await test("Kernel restart retains identity, actions, messages and generation",()=>{
            var path=Fresh(root,"restart");ParticipantSession session;string actionId,messageId;long generation;
            using(var service=new CoordinationService(path)){session=service.Register("register","stable","group");var actor=service.Authenticate(session);var action=service.CreateAction(actor,"work","create");actionId=service.CloseAction(actor,action.Id,ActionOutcome.Failed,"Execution failed",1,"close").Id;messageId=service.SendMessage(actor,"system","report","send").Message.Id;generation=service.Snapshot().Generation;}
            using(var service=new CoordinationService(path)){Check(service.Authenticate(session).Address==session.Id,"Identity lost.");var action=service.ReadAction(actionId);Check(action.Outcome==ActionOutcome.Failed&&action.Explanation=="Execution failed","Outcome lost.");Check(service.ReadMessage(messageId).Message.Text=="report"&&service.Snapshot().Generation==generation,"Message or generation lost.");Check(service.Register("register","stable","group").Capability==session.Capability,"Registration retry changed capability after restart.");
                var next=service.Register("new-incarnation","stable","group");Check(next.Id!=session.Id&&Guid.TryParse(next.Id,out _),"Intentional new registration reused incarnation.");}
            return Task.CompletedTask;
        });
        await AdmissionTests(test,root);
    }
    private static async Task AdmissionTests(Func<string,Func<Task>,Task> test,string root)
    {
        await test("Kernel admission bundles FIFO independent and owner auth",()=>{
            var dir=Fresh(root,"admission");using var service=new CoordinationService(dir);
            var a=service.Authenticate(service.Register("a"));var b=service.Authenticate(service.Register("b"));var c=service.Authenticate(service.Register("c"));
            ResourceRequest R(string id,string mode)=>new("id:"+id,mode);
            Refuses<ArgumentException>(()=>service.RequestResources(a,[new ResourceRequest(null!,"read")],"invalid-null"));
            var first=service.RequestResources(a,[R("x","write"),R("y","write")],"first");
            Check(first.State=="admitted"&&Guid.TryParse(first.Id,out _),"Bundle was not admitted.");
            var generation=service.Snapshot().Generation;
            var waiting=service.RequestResources(b,[R("x","read"),R("z","write")],"waiting");
            Check(waiting.State=="pending"&&service.Snapshot().Generation==generation&&service.Snapshot().Entries.Single(e=>e.Id==b.Address).Holds.Count==0,"Pending bundle projected a partial hold, including its free resource.");
            var reader=service.RequestResources(c,[R("x","read")],"reader");
            Check(reader.State=="pending"&&service.Snapshot().Generation==generation,"Reader passed older overlapping request.");
            var independent=service.RequestResources(c,[R("other","write")],"independent");
            Check(independent.State=="admitted","Independent scope did not progress.");
            Refuses<UnauthorizedAccessException>(()=>service.ReleaseResources(c,first.Id,1,"forge"));
            Refuses<UnauthorizedAccessException>(()=>service.ReadAdmission(c,first.Id));
            Refuses<InvalidOperationException>(()=>service.RequestResources(a,[R("x","read")],"upgrade"));
            var retry=service.RequestResources(a,[R("x","write"),R("y","write")],"first");
            Check(retry.Id==first.Id&&retry.State==first.State&&retry.Revision==first.Revision&&retry.Resources.SequenceEqual(first.Resources),"Duplicate request changed original receipt.");
            Refuses<InvalidOperationException>(()=>service.RequestResources(a,[R("x","read")],"first"));
            service.ReleaseResources(a,first.Id,1,"release");
            Check(service.ReadAdmission(b,waiting.Id).State=="admitted"&&service.ReadAdmission(c,reader.Id).State=="admitted","Compatible readers were not promoted.");
            Check(service.Snapshot().Entries.Single(e=>e.Id==c.Address).Holds.Count==2,"Other bundle hold was lost.");
            service.ReleaseResources(c,reader.Id,2,"release-reader");
            Check(service.Snapshot().Entries.Single(e=>e.Id==c.Address).Holds.ContainsKey("id:other"),"Bundle release removed unrelated hold.");
            return Task.CompletedTask;
        });
        await test("Kernel older writer blocks newer reader",()=>{
            using var service=new CoordinationService(Fresh(root,"admission-writer-fifo"));
            var a=service.Authenticate(service.Register("a"));var b=service.Authenticate(service.Register("b"));var c=service.Authenticate(service.Register("c"));
            var first=service.RequestResources(a,[new("id:shared","read")],"reader-a");
            var writer=service.RequestResources(b,[new("id:shared","write")],"writer");
            var later=service.RequestResources(c,[new("id:shared","read")],"reader-c");
            Check(writer.State=="pending"&&later.State=="pending","New reader passed queued writer.");
            service.ReleaseResources(a,first.Id,1,"release-a");
            Check(service.ReadAdmission(b,writer.Id).State=="admitted"&&service.ReadAdmission(c,later.Id).State=="pending","FIFO writer was skipped.");
            service.ReleaseResources(b,writer.Id,2,"release-b");
            Check(service.ReadAdmission(c,later.Id).State=="admitted","Reader failed to wake after writer release.");
            return Task.CompletedTask;
        });
        await test("Kernel admission path boundaries and alias refusal",()=>{
            var rootPath=Fresh(root,"admission-paths");
            var parent=Path.Combine(rootPath,"parent");var sibling=Path.Combine(rootPath,"parent2");
            Directory.CreateDirectory(parent);Directory.CreateDirectory(sibling);
            var child=Path.Combine(parent,"child.txt");File.WriteAllText(child,"x");
            var other=Path.Combine(sibling,"other.txt");File.WriteAllText(other,"y");
            using var service=new CoordinationService(Fresh(root,"admission-path-store"));
            var a=service.Authenticate(service.Register("a"));var b=service.Authenticate(service.Register("b"));
            var c=service.Authenticate(service.Register("c"));
            var dirHold=service.RequestResources(a,[new("file:"+parent,"write")],"dir");
            Check(service.RequestResources(b,[new("file:"+child.ToUpperInvariant().Replace('\\','/'),"read")],"child").State=="pending","Case and slash alias escaped directory hold.");
            var shortBuffer=new System.Text.StringBuilder(32768);
            var shortLength=GetShortPathName(child,shortBuffer,(uint)shortBuffer.Capacity);
            if(shortLength>0&&shortLength<shortBuffer.Capacity&&!string.Equals(shortBuffer.ToString(),child,StringComparison.OrdinalIgnoreCase))
                Check(service.RequestResources(c,[new("file:"+shortBuffer,"read")],"short-alias").State=="pending","Short directory alias escaped held scope.");
            else Console.WriteLine("SKIP 8.3 alias fixture: short path unavailable.");
            Check(service.RequestResources(b,[new("file:"+other,"write")],"sibling").State=="admitted","Sibling path conflicted across component boundary.");
            var hard=Path.Combine(sibling,"hard.txt");
            if(!CreateHardLink(hard,child,IntPtr.Zero)){ Console.WriteLine("SKIP hardlink fixture: link creation unavailable."); return Task.CompletedTask; }
            Refuses<NotSupportedException>(()=>service.RequestResources(b,[new("file:"+hard,"write")],"hard"));
            Refuses<NotSupportedException>(()=>service.RequestResources(c,[new("file:"+sibling,"write")],"hard-directory"));
            service.ReleaseResources(a,dirHold.Id,1,"release-dir");
            Check(service.ListAdmissions(b).Single(x=>x.Resources.Any(r=>r.Resource.EndsWith("CHILD.TXT",StringComparison.Ordinal))).State=="suspended","New hard link did not suspend pending file after committed release.");
            Check(service.Snapshot().Entries.Single(e=>e.Id==b.Address).Holds.Count==1,"Suspended file projected a hold or lost unrelated bundle.");
            return Task.CompletedTask;
        });
        await test("Kernel stale queued owner suspends on promotion",()=>{
            var dir=Fresh(root,"admission-stale");ParticipantSession ownerSession,queuedSession;string held,waiting;
            using(var service=new CoordinationService(dir)){
                ownerSession=service.Register("owner");queuedSession=service.Register("queued");
                var owner=service.Authenticate(ownerSession);var queued=service.Authenticate(queuedSession);
                held=service.RequestResources(owner,[new("id:stale","write")],"held").Id;
                waiting=service.RequestResources(queued,[new("id:stale","write")],"waiting").Id;
            }
            SetParticipantTicks(Path.Combine(dir,"coordination.sqlite"),queuedSession.Id,0);
            using(var service=new CoordinationService(dir)){
                var owner=service.Authenticate(ownerSession);
                Refuses<UnauthorizedAccessException>(()=>service.Authenticate(queuedSession));
                var before=service.Snapshot().Generation;
                service.ReleaseResources(owner,held,1,"release");
                var suspended=service.ReadAdmission(service.TrustedUser(),waiting);
                Check(suspended.State=="suspended"&&suspended.Reason is not null&&service.Snapshot().Generation==before+1,"Stale owner was admitted or private suspension changed generation.");
                Check(service.Snapshot().Entries.All(e=>!e.Holds.ContainsKey("id:stale")),"Suspended work projected a hold.");
            }
            return Task.CompletedTask;
        });
        await test("Kernel admission cancellation restart and concurrent writers",async()=>{
            var dir=Fresh(root,"admission-restart");ParticipantSession stale;string held,queued;
            using(var service=new CoordinationService(dir)){
                var a=service.Authenticate(service.Register("a"));stale=service.Register("b");var b=service.Authenticate(stale);
                held=service.RequestResources(a,[new("id:same","write")],"held").Id;
                queued=service.RequestResources(b,[new("id:same","write")],"queued").Id;
                var generation=service.Snapshot().Generation;
                service.CancelAdmission(b,queued,1,"cancel");
                Check(service.Snapshot().Generation==generation,"Pending cancel advanced projection.");
                Refuses<InvalidOperationException>(()=>service.CancelAdmission(b,queued,1,"again"));
                queued=service.RequestResources(b,[new("id:same","write")],"queued-2").Id;
            }
            using(var service=new CoordinationService(dir)){
                var a=service.Authenticate(service.Register("a-new"));
                // The original admitted owner is preserved; a different registration cannot release it.
                Refuses<UnauthorizedAccessException>(()=>service.ReleaseResources(a,held,1,"forged"));
                Check(service.Snapshot().Entries.Any(e=>e.Holds.ContainsKey("id:same")),"Restart lost admitted hold.");
            }
            using(var service=new CoordinationService(Fresh(root,"admission-race"))){
                var a=service.Authenticate(service.Register("a"));var b=service.Authenticate(service.Register("b"));
                var results=await Task.WhenAll(Task.Run(()=>service.RequestResources(a,[new("id:race","write")],"race-a")),Task.Run(()=>service.RequestResources(b,[new("id:race","write")],"race-b")));
                Check(results.Count(r=>r.State=="admitted")==1&&results.Count(r=>r.State=="pending")==1,"Concurrent writers both admitted or both waited.");
            }
        });
    }
    private static void SetParticipantTicks(string path,string id,long ticks){
        var rc=sqlite3_open_v2(path,out var db,2,IntPtr.Zero);if(rc!=0)throw new IOException("Test SQLite open failed.");
        try{rc=sqlite3_exec(db,"UPDATE participants SET created_ticks="+ticks+" WHERE id='"+id+"'",IntPtr.Zero,IntPtr.Zero,out var error);if(rc!=0)throw new IOException("Test SQLite update failed: "+Marshal.PtrToStringUTF8(error));}
        finally{sqlite3_close(db);}
    }
    private static void SetPragma(string path,string pragma){
        var rc=sqlite3_open_v2(path,out var db,2,IntPtr.Zero);if(rc!=0)throw new IOException("Test SQLite open failed.");
        try{rc=sqlite3_exec(db,"PRAGMA "+pragma,IntPtr.Zero,IntPtr.Zero,out var error);if(rc!=0)throw new IOException("Test SQLite pragma failed: "+Marshal.PtrToStringUTF8(error));}
        finally{sqlite3_close(db);}
    }
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out IntPtr db,int flags,IntPtr vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,IntPtr callback,IntPtr context,out IntPtr error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close(IntPtr db);
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string name,string existing,IntPtr reserved);
    [DllImport("kernel32.dll",EntryPoint="GetShortPathNameW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetShortPathName(string longPath,System.Text.StringBuilder shortPath,uint size);}













