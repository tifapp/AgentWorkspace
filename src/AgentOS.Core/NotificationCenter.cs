using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Core;
public sealed record LocalNotice(string Identity,string Project,string? WorkId,string? DecisionId,string Kind,string Title,DateTimeOffset At,bool Delivered);
public sealed class NotificationCenter
{
 private sealed class Store { public int Schema {get;set;}=1; public List<LocalNotice> Notices {get;set;}=[]; public int QuietFromMinutes {get;set;}=1320; public int QuietUntilMinutes {get;set;}=480; public bool Enabled {get;set;}=true; }
 private readonly string path; private readonly object gate=new(); private readonly Store store;
 public TimeOnly QuietFrom {get;private set;}=new(22,0); public TimeOnly QuietUntil {get;private set;}=new(8,0);
 public NotificationCenter(string? path=null)
 {this.path=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","notifications.json");
  using var file=File.Exists(this.path)?new FileStream(this.path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete):null;
  store=file==null?new():JsonSerializer.Deserialize<Store>(file,JsonFormat.Options)??throw new InvalidDataException("Empty notification history.");
  if(store.Schema!=1||store.Notices==null||store.QuietFromMinutes is <0 or >=1440||store.QuietUntilMinutes is <0 or >=1440)throw new InvalidDataException("Unsupported notification history version.");
  QuietFrom=TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(store.QuietFromMinutes));QuietUntil=TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(store.QuietUntilMinutes));}
 public bool Enabled {get{lock(gate)return store.Enabled;}} public void ConfigureEnabled(bool value){lock(gate){store.Enabled=value;Save();}}
 public void ConfigureQuietHours(TimeOnly from,TimeOnly until){lock(gate){QuietFrom=from;QuietUntil=until;store.QuietFromMinutes=from.Hour*60+from.Minute;store.QuietUntilMinutes=until.Hour*60+until.Minute;Save();}}
 public IReadOnlyList<LocalNotice> History {get{lock(gate)return store.Notices.ToArray();}}
 public bool IsQuiet(DateTimeOffset instant,TimeZoneInfo? zone=null)
 {var time=TimeZoneInfo.ConvertTime(instant,zone??TimeZoneInfo.Local).TimeOfDay;var from=QuietFrom.ToTimeSpan();var until=QuietUntil.ToTimeSpan();
  return from==until||(from<until?time>=from&&time<until:time>=from||time<until);}
 public LocalNotice? Record(string identity,string project,string? workId,string? decisionId,string kind,string title,DateTimeOffset? at=null,bool? delivered=null)
 {if(string.IsNullOrWhiteSpace(identity)||string.IsNullOrWhiteSpace(project))throw new ArgumentException("Event identity and project are required.");
  lock(gate){if(store.Notices.Any(x=>x.Identity==identity&&string.Equals(x.Project,project,StringComparison.OrdinalIgnoreCase)))return null;
   var instant=at??DateTimeOffset.UtcNow;title=Sanitize(title);
   var notice=new LocalNotice(identity,project,workId,decisionId,kind,title,instant,delivered??(store.Enabled&&!IsQuiet(instant)));store.Notices.Add(notice);
   if(store.Notices.Count>500)store.Notices.RemoveRange(0,store.Notices.Count-500);
   Save();return notice;}}
 public static string Sanitize(string? value){if(string.IsNullOrWhiteSpace(value))return "Task status changed";var clean=new string(value.Select(c=>char.IsControl(c)?' ':c).ToArray()).Trim();return clean.Length>100?clean[..100]:clean.Length==0?"Task status changed":clean;}
 public bool MarkDelivered(LocalNotice notice){lock(gate){var i=store.Notices.FindIndex(x=>x.Identity==notice.Identity&&string.Equals(x.Project,notice.Project,StringComparison.OrdinalIgnoreCase));if(i<0||store.Notices[i].Delivered)return false;store.Notices[i]=store.Notices[i] with {Delivered=true};Save();return true;}}
 private void Save(){Directory.CreateDirectory(Path.GetDirectoryName(path)!);var next=path+".next";
   using(var file=new FileStream(next,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,store,JsonFormat.Options);file.Flush(true);}File.Move(next,path,true);}
}

public sealed class ProjectNotificationWatcher:IDisposable
{
 private readonly NotificationCenter center;private readonly Action<LocalNotice> deliver;private readonly object gate=new();
 private FileSystemWatcher? watcher;private Timer? debounce;private string? project,directory;private readonly Dictionary<string,WorkStatus> work=new();private readonly HashSet<string> pending=[];private bool disposed;private DateTimeOffset firstChange;
 public ProjectNotificationWatcher(NotificationCenter center,Action<LocalNotice> deliver){this.center=center;this.deliver=deliver;}
 public void Select(string? projectPath,string? dataRoot)
 {lock(gate){watcher?.Dispose();watcher=null;debounce?.Dispose();debounce=null;project=null;directory=null;work.Clear();pending.Clear();
  if(disposed||string.IsNullOrWhiteSpace(projectPath)||!Directory.Exists(projectPath))return;
  var full=Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..24];
  var dir=Path.Combine(dataRoot??RememberedRoot(hash)??ProjectRuntime.DefaultDataRoot,hash);if(!Directory.Exists(dir))return;
  var initial=ReadState(Path.Combine(dir,"state.json"));
  project=full;directory=dir;if(initial!=null&&string.Equals(initial.ProjectPath,full,StringComparison.OrdinalIgnoreCase))Prime(initial);
  watcher=new FileSystemWatcher(dir){Filter="*.json",IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.CreationTime};
  watcher.Changed+=Changed;watcher.Created+=Changed;watcher.Renamed+=Renamed;watcher.EnableRaisingEvents=true;}Scan();}
 private static string? RememberedRoot(string hash)
 {try{var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","project-locations",hash+".json");using var doc=JsonDocument.Parse(File.ReadAllText(path));return doc.RootElement.GetProperty("DataRoot").GetString();}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException){return null;}}
 private void Renamed(object sender,RenamedEventArgs e)=>Changed(sender,e);
 private void Changed(object sender,FileSystemEventArgs e)
 {if(e.Name is not ("state.json" or "interactions.json")&&!(e.FullPath.Contains("external-effects"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&e.Name?.EndsWith(".json",StringComparison.OrdinalIgnoreCase)==true))return;
  lock(gate)if(!disposed){if(debounce==null)firstChange=DateTimeOffset.UtcNow;debounce?.Dispose();var delay=DateTimeOffset.UtcNow-firstChange>=TimeSpan.FromSeconds(2)?1:180;debounce=new Timer(_=>Scan(),null,delay,Timeout.Infinite);}}
 public void Scan()
 {List<LocalNotice> outgoing=[];lock(gate){debounce?.Dispose();debounce=null;if(disposed||directory==null||project==null)return;
  var state=ReadState(Path.Combine(directory,"state.json"));if(state==null||!string.Equals(state.ProjectPath,project,StringComparison.OrdinalIgnoreCase))return;
  foreach(var item in state.Work){if(work.TryGetValue(item.Id,out var old)&&old!=item.Status&&item.Status is WorkStatus.Completed or WorkStatus.Failed or WorkStatus.Waiting or WorkStatus.Private)
   Add($"work:{item.Id}:{item.Status}:{item.UpdatedAt.UtcTicks}",item.Id,null,item.Status.ToString(),"Task "+item.Status.ToString().ToLowerInvariant(),item.UpdatedAt);work[item.Id]=item.Status;}
  var active=new HashSet<string>();foreach(var decision in state.Decisions.Where(x=>x.Status==DecisionStatus.Pending)){var id="decision:"+decision.Id;active.Add(id);if(!pending.Contains(id))Add(id,decision.WorkId,decision.Id,"Decision","A task needs a decision",decision.CreatedAt);}
  foreach(var item in ReadInteractions(Path.Combine(directory,"interactions.json"))){if(!IsActionable(item)||!state.Work.Any(w=>w.Id==(item.Kind==InteractionKind.Peer?item.TargetWorkId:item.WorkId)))continue;var id="interaction:"+item.Kind+":"+item.Id;active.Add(id);if(!pending.Contains(id))Add(id,item.Kind==InteractionKind.Peer?item.TargetWorkId:item.WorkId,item.Id,item.Kind.ToString(),Title(item.Kind),item.CreatedAt);}
  foreach(var effect in ReadEffects(directory).Where(x=>x.State==ExternalEffectState.Prepared)){var id="effect:"+effect.Id;active.Add(id);if(!pending.Contains(id))Add(id,null,effect.Id,"ExternalReview","An external action needs review",effect.UpdatedAt);}
  pending.Clear();pending.UnionWith(active);
  void Add(string id,string? workId,string? requestId,string kind,string title,DateTimeOffset at){var notice=center.Record(id,project,workId,requestId,kind,title,at,false);if(notice!=null&&center.Enabled&&!center.IsQuiet(DateTimeOffset.UtcNow))outgoing.Add(notice);}
 }foreach(var notice in outgoing)deliver(notice);}
 private static string Title(InteractionKind kind)=>kind switch{InteractionKind.Clarification=>"A task needs clarification",InteractionKind.Peer=>"A peer request needs a reply",InteractionKind.Followup=>"A followup needs review",InteractionKind.Obligation=>"A required followup needs review",_=>"A task needs attention"};
 private static bool IsActionable(TaskInteraction item)=>item.Status==InteractionStatus.Pending&&(!item.Deadline.HasValue||item.Deadline>DateTimeOffset.UtcNow)&&!string.IsNullOrWhiteSpace(item.Id)&&!string.IsNullOrWhiteSpace(item.WorkId)&&(item.Kind is InteractionKind.Clarification or InteractionKind.Peer or InteractionKind.Followup||item.Kind==InteractionKind.Obligation&&item.Required);
 private void Prime(ProjectState state){foreach(var item in state.Work)work[item.Id]=item.Status;}
 public bool IsCurrent(LocalNotice notice)
 {lock(gate){if(disposed||project==null||directory==null||!string.Equals(project,notice.Project,StringComparison.OrdinalIgnoreCase))return false;var state=ReadState(Path.Combine(directory,"state.json"));if(state==null||!string.Equals(state.ProjectPath,project,StringComparison.OrdinalIgnoreCase))return false;
  if(notice.Identity.StartsWith("interaction:",StringComparison.Ordinal))return ReadInteractions(Path.Combine(directory,"interactions.json")).Any(x=>IsActionable(x)&&x.Id==notice.DecisionId&&state.Work.Any(w=>w.Id==(x.Kind==InteractionKind.Peer?x.TargetWorkId:x.WorkId))&&notice.Identity=="interaction:"+x.Kind+":"+x.Id&&notice.WorkId==(x.Kind==InteractionKind.Peer?x.TargetWorkId:x.WorkId));
  if(notice.Identity.StartsWith("decision:",StringComparison.Ordinal))return state.Decisions.Any(x=>notice.Identity=="decision:"+x.Id&&x.Id==notice.DecisionId&&x.WorkId==notice.WorkId&&x.Status==DecisionStatus.Pending);
  if(notice.Identity.StartsWith("effect:",StringComparison.Ordinal))return ReadEffects(directory).Any(x=>notice.Identity=="effect:"+x.Id&&x.Id==notice.DecisionId&&x.State==ExternalEffectState.Prepared);
  return notice.WorkId!=null&&state.Work.Any(x=>x.Id==notice.WorkId&&x.Status.ToString()==notice.Kind);}}
 private static ProjectState? ReadState(string path){var state=ReadBounded<ProjectState>(path,8_000_000);return state?.Schema==2&&state.Work!=null&&state.Decisions!=null?state:null;}
 private sealed class InteractionJournal{public int Schema{get;set;}public List<TaskInteraction>? Items{get;set;}}
 private static IReadOnlyList<TaskInteraction> ReadInteractions(string path){var journal=ReadBounded<InteractionJournal>(path,4_000_000);if(journal?.Schema!=1||journal.Items==null||journal.Items.Count>10000||journal.Items.Any(x=>x==null)||journal.Items.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=journal.Items.Count)return [];return journal.Items;}
 private static IReadOnlyList<EffectIntent> ReadEffects(string root){var dir=Path.Combine(root,"external-effects");if(!Directory.Exists(dir))return [];var effects=new List<EffectIntent>();try{foreach(var path in Directory.EnumerateFiles(dir,"*.json").Take(1001)){if(effects.Count>=1000)return [];var item=ReadBounded<EffectIntent>(path,200_000);if(item!=null&&!string.IsNullOrWhiteSpace(item.Id))effects.Add(item);}}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return [];}return effects;}
 private static T? ReadBounded<T>(string path,long limit)where T:class{for(var i=0;i<3;i++)try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(file.Length>limit)return null;return JsonSerializer.Deserialize<T>(file,JsonFormat.Options);}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}catch(IOException){Thread.Sleep(30);}catch(UnauthorizedAccessException){Thread.Sleep(30);}catch(JsonException){return null;}return null;}
 public void Dispose(){lock(gate){disposed=true;watcher?.Dispose();debounce?.Dispose();}}
}




