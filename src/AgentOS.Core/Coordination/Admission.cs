using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace AgentOS.Core.Coordination;
public sealed record ResourceRequest(string Resource,string Mode);
public sealed record AdmissionDetail(string Id,string Owner,string State,long Revision,IReadOnlyList<ResourceRequest> Resources,string? Reason,DateTimeOffset CreatedAt);
internal sealed class CoordinationAdmission
{
 readonly CoordinationStore store; readonly CoordinationIdentity identity;
 internal CoordinationAdmission(CoordinationStore s,CoordinationIdentity i){store=s;identity=i;}
 internal static void CreateSchema(CoordinationStore s){
  s.Exec("CREATE TABLE admissions(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,owner TEXT NOT NULL REFERENCES participants(id),state TEXT NOT NULL CHECK(state IN ('pending','admitted','suspended','released','canceled')),revision INTEGER NOT NULL CHECK(revision>=1),reason TEXT,created_at TEXT NOT NULL)");
  s.Exec("CREATE TABLE admission_resources(admission_id TEXT NOT NULL REFERENCES admissions(id),resource TEXT NOT NULL,mode TEXT NOT NULL CHECK(mode IN ('read','write')),path TEXT,identity TEXT,is_directory INTEGER NOT NULL,PRIMARY KEY(admission_id,resource))");
  s.Exec("CREATE INDEX admissions_state_seq ON admissions(state,seq)");
 }
 record Claim(string Key,string Mode,string? Path,string? Identity,bool Directory);
 record Bundle(string Id,string Owner,string State,List<Claim> Claims);
 public AdmissionDetail Request(Actor actor,IReadOnlyList<ResourceRequest> resources,string key){
  if(actor.Kind!=AuthorityKind.Participant)throw new UnauthorizedAccessException("Participant owner required.");
  if(resources is null||resources.Count==0)throw new ArgumentException("Nonempty bundle required.");
  var who=identity.Address(actor);
  var requested=resources.ToArray();
  bool changed=false;
  var result=store.Command(who,"resource-request",key,requested,tx=>{
   identity.Address(actor);
   var claims=requested.Select(Canonical).OrderBy(c=>c.Key,StringComparer.Ordinal).ToList();
   if(claims.Select(c=>c.Key).Distinct(StringComparer.Ordinal).Count()!=claims.Count)throw new ArgumentException("Duplicate resources or conversion.");
   foreach(var claim in claims)Revalidate(claim);
   if(Load(tx,"admitted","pending","suspended").Where(b=>b.Owner==who).Any(b=>b.Claims.Any(c=>claims.Any(n=>Overlap(c,n)))))throw new InvalidOperationException("Release overlapping owned bundle before conversion.");
   var id=CoordinationStore.Id();tx.Run("INSERT INTO admissions(id,owner,state,revision,created_at) VALUES(?,?,'pending',1,?)",id,who,CoordinationStore.Now());
   foreach(var c in claims)tx.Run("INSERT INTO admission_resources VALUES(?,?,?,?,?,?)",id,c.Key,c.Mode,c.Path,c.Identity,c.Directory?"1":"0");
   if(!Load(tx,"admitted","pending","suspended").Where(b=>b.Id!=id).Any(b=>b.Claims.Any(c=>claims.Any(n=>Conflict(c,n))))){tx.Run("UPDATE admissions SET state='admitted' WHERE id=?",id);changed=true;tx.Event(who,"resource-admitted",id,new{resources=claims.Select(c=>new{c.Key,c.Mode})});}
   else tx.Event(who,"resource-pending",id,new{resources=claims.Select(c=>new{c.Key,c.Mode})});
   return JsonSerializer.Serialize(ReadCore(tx,id));
  },()=>changed);
  return JsonSerializer.Deserialize<AdmissionDetail>(result)!;
 }
 public AdmissionDetail Finish(Actor actor,string id,long expectedRevision,string key,bool cancel){
  var who=identity.Address(actor);bool changed=false;
  var result=store.Command(who,cancel?"resource-cancel":"resource-release",key,new{id,expectedRevision},tx=>{
   identity.Address(actor);var item=ReadCore(tx,id);
   if(item.Owner!=who)throw new UnauthorizedAccessException("Only bundle owner can finish admission.");
   if(item.Revision!=expectedRevision)throw new InvalidOperationException("Admission revision conflict.");
   if(cancel && item.State is not ("pending" or "suspended"))throw new InvalidOperationException("Only pending work can be canceled.");
   if(!cancel && item.State!="admitted")throw new InvalidOperationException("Only admitted work can be released.");
   changed=!cancel;tx.Run("UPDATE admissions SET state=?,revision=revision+1,reason=NULL WHERE id=?",cancel?"canceled":"released",id);
   tx.Event(who,cancel?"resource-canceled":"resource-released",id,new{expectedRevision});Promote(tx,ref changed);
   return JsonSerializer.Serialize(ReadCore(tx,id));
  },()=>changed);return JsonSerializer.Deserialize<AdmissionDetail>(result)!;
 }

 void Promote(CoordinationTransaction tx,ref bool changed){
  var blockers=Load(tx,"admitted");
  foreach(var next in Load(tx,"pending","suspended")){
   if(next.State=="suspended"||blockers.Any(b=>b.Claims.Any(c=>next.Claims.Any(n=>Conflict(c,n))))){blockers.Add(next);continue;}
   try{RevalidateOwner(tx,next.Owner);foreach(var c in next.Claims)Revalidate(c);}
   catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException){
    tx.Run("UPDATE admissions SET state='suspended',revision=revision+1,reason=? WHERE id=?",e.Message,next.Id);
    tx.Event(next.Owner,"resource-suspended",next.Id,new{reason=e.Message});blockers.Add(next);continue;
   }
   tx.Run("UPDATE admissions SET state='admitted',revision=revision+1 WHERE id=?",next.Id);
   tx.Event(next.Owner,"resource-admitted",next.Id,new{promoted=true});changed=true;blockers.Add(next);
  }
 }
 void RevalidateOwner(CoordinationTransaction tx,string owner){
  var row=tx.Rows("SELECT pid,created_ticks FROM participants WHERE id=?",owner).SingleOrDefault()??throw new UnauthorizedAccessException("Owner missing.");
  if(!store.IsLive(int.Parse(row[0]!,CultureInfo.InvariantCulture),long.Parse(row[1]!,CultureInfo.InvariantCulture)))throw new UnauthorizedAccessException("Owner process dead or unknown.");
 }
 public AdmissionDetail Read(Actor actor,string id)
 {
  var who=identity.Address(actor);
  lock(store.Gate)
  {
   var item=ReadCore(null,id);
   if(actor.Kind==AuthorityKind.Participant&&item.Owner!=who)
    throw new UnauthorizedAccessException("Admission belongs to another actor.");
   return item;
  }
 }
 public IReadOnlyList<AdmissionDetail> List(Actor actor)
 {
  var who=identity.Address(actor);
  lock(store.Gate)
  {
   var rows=actor.Kind==AuthorityKind.Participant
    ?store.Rows("SELECT id FROM admissions WHERE owner=? ORDER BY seq",who)
    :store.Rows("SELECT id FROM admissions ORDER BY seq");
   return rows.Select(r=>ReadCore(null,r[0]!)).ToArray();
  }
 }
 AdmissionDetail ReadCore(CoordinationTransaction? tx,string id){
  var row=(tx?.Rows("SELECT owner,state,revision,reason,created_at FROM admissions WHERE id=?",id)??store.Rows("SELECT owner,state,revision,reason,created_at FROM admissions WHERE id=?",id)).SingleOrDefault()??throw new KeyNotFoundException("Admission missing.");
  var claims=(tx?.Rows("SELECT resource,mode FROM admission_resources WHERE admission_id=? ORDER BY resource",id)??store.Rows("SELECT resource,mode FROM admission_resources WHERE admission_id=? ORDER BY resource",id)).Select(r=>new ResourceRequest(r[0]!,r[1]!)).ToArray();
  return new(id,row[0]!,row[1]!,long.Parse(row[2]!,CultureInfo.InvariantCulture),claims,row[3],DateTimeOffset.Parse(row[4]!,CultureInfo.InvariantCulture));
 }
 internal static IReadOnlyDictionary<string,string> ProjectHolds(CoordinationStore store,string owner){
  var holds=new Dictionary<string,string>(StringComparer.Ordinal);
  foreach(var row in store.Rows("SELECT r.resource,r.mode FROM admissions a JOIN admission_resources r ON r.admission_id=a.id WHERE a.owner=? AND a.state='admitted' ORDER BY a.seq",owner))holds[row[0]!]=row[1]!;
  return holds;
 }
 static List<Bundle> Load(CoordinationTransaction tx,params string[] states)=>tx.Rows("SELECT id,owner,state FROM admissions ORDER BY seq").Where(r=>states.Contains(r[2]!)).Select(r=>new Bundle(r[0]!,r[1]!,r[2]!,tx.Rows("SELECT resource,mode,path,identity,is_directory FROM admission_resources WHERE admission_id=?",r[0]).Select(c=>new Claim(c[0]!,c[1]!,c[2],c[3],c[4]=="1")).ToList())).ToList();
 static bool Conflict(Claim a,Claim b)=>(a.Mode=="write"||b.Mode=="write")&&Overlap(a,b);
 static bool Overlap(Claim a,Claim b){
  if(a.Path is null||b.Path is null)return a.Key==b.Key;
  if(a.Identity==b.Identity)return true;
  return a.Path==b.Path||(a.Directory&&Descendant(b.Path,a.Path))||(b.Directory&&Descendant(a.Path,b.Path));
 }
 static bool Descendant(string child,string parent)=>child.StartsWith(parent.EndsWith('\\')?parent:parent+"\\",StringComparison.OrdinalIgnoreCase);
 static Claim Canonical(ResourceRequest request){
  if(request is null||request.Mode is not ("read" or "write"))throw new ArgumentException("Mode must be read or write.");
  if(request.Resource is null)throw new ArgumentException("Resource required.");
  if(request.Resource.StartsWith("id:",StringComparison.Ordinal)){
   var key=request.Resource[3..];if(string.IsNullOrWhiteSpace(key)||key.Any(char.IsControl))throw new ArgumentException("Invalid id resource.");
   return new("id:"+key,request.Mode,null,null,false);
  }
  if(!request.Resource.StartsWith("file:",StringComparison.Ordinal))throw new ArgumentException("Resource must begin file: or id:.");
  var raw=request.Resource[5..];if(raw.Length==0||raw.Any(char.IsControl))throw new ArgumentException("Invalid file resource.");
  if(!OperatingSystem.IsWindows()||!Path.IsPathFullyQualified(raw)||raw.StartsWith(@"\\",StringComparison.Ordinal))throw new NotSupportedException("Only fully qualified local Windows paths are supported.");
  var full=Path.GetFullPath(raw);var root=Path.GetPathRoot(full)!;var current=root;
  foreach(var part in full[root.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries)){
   current=Path.Combine(current,part);
   if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse path is ambiguous.");
  }
  var directory=Directory.Exists(full);if(!directory&&!File.Exists(full))throw new IOException("Resource path must exist.");
  using var handle=Open(full);
  var buffer=new char[32768];var length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Length,0);
  if(length==0||length>=buffer.Length)throw new Win32Exception(Marshal.GetLastWin32Error());
  var final=new string(buffer,0,(int)length);
  if(final.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Network paths require a verified adapter.");
  if(final.StartsWith(@"\\?\",StringComparison.OrdinalIgnoreCase))final=final[4..];
  final=Path.TrimEndingDirectorySeparator(final).ToUpperInvariant();
  if(!GetFileInformationByHandle(handle,out var info)||(info.IndexHigh==0&&info.IndexLow==0))throw new IOException("Stable file identity unavailable.");
  if(!directory&&info.Links!=1)throw new NotSupportedException("Hard-linked files require verified containment.");
  if(directory)VerifyDirectory(full);
  var fileIdentity=$"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
  return new("file:"+final,request.Mode,final,fileIdentity,directory);
 }
 static void VerifyDirectory(string root){
  var pending=new Stack<string>();pending.Push(root);var count=0;
  while(pending.Count>0){
   foreach(var entry in Directory.EnumerateFileSystemEntries(pending.Pop())){
    if(++count>10000)throw new NotSupportedException("Directory too large for bounded identity validation.");
    var attributes=File.GetAttributes(entry);
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new NotSupportedException("Directory contains a reparse path.");
    if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
    else{
     using var handle=Open(entry);
     if(!GetFileInformationByHandle(handle,out var info)||info.Links!=1)
      throw new NotSupportedException("Directory contains a hard-linked or unverified file.");
    }
   }
  }
 }
 static void Revalidate(Claim claim){
  if(claim.Path is null)return;
  var latest=Canonical(new ResourceRequest("file:"+claim.Path,claim.Mode));
  if(latest.Key!=claim.Key||latest.Identity!=claim.Identity||latest.Directory!=claim.Directory)throw new IOException("Queued resource identity changed.");
 }
 static SafeFileHandle Open(string path){var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());return handle;}
 [StructLayout(LayoutKind.Sequential)]struct FileInfo{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,char[] path,uint size,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo info);
}