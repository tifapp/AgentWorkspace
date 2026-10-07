using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Core;
public sealed record LocalNotice(string Identity,string Project,string? WorkId,string? DecisionId,string Kind,string Title,DateTimeOffset At,bool Delivered);
public sealed class NotificationCenter
{
 private sealed class Store { public int Schema {get;set;}=1; public List<LocalNotice> Notices {get;set;}=[]; public int QuietFromMinutes {get;set;}=1320; public int QuietUntilMinutes {get;set;}=480; }
 private readonly string path; private readonly object gate=new(); private readonly Store store;
 public TimeOnly QuietFrom {get;set;}=new(22,0); public TimeOnly QuietUntil {get;set;}=new(8,0);
 public NotificationCenter(string? path=null)
 {this.path=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","notifications.json");
  using var file=File.Exists(this.path)?new FileStream(this.path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete):null;
  store=file==null?new():JsonSerializer.Deserialize<Store>(file,JsonFormat.Options)??throw new InvalidDataException("Empty notification history.");
  if(store.Schema!=1||store.Notices==null||store.QuietFromMinutes is <0 or >=1440||store.QuietUntilMinutes is <0 or >=1440)throw new InvalidDataException("Unsupported notification history version.");
  QuietFrom=TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(store.QuietFromMinutes));QuietUntil=TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(store.QuietUntilMinutes));}
 public void ConfigureQuietHours(TimeOnly from,TimeOnly until){lock(gate){QuietFrom=from;QuietUntil=until;store.QuietFromMinutes=from.Hour*60+from.Minute;store.QuietUntilMinutes=until.Hour*60+until.Minute;Save();}}
 public IReadOnlyList<LocalNotice> History {get{lock(gate)return store.Notices.ToArray();}}
 public bool IsQuiet(DateTimeOffset instant,TimeZoneInfo? zone=null)
 {var time=TimeZoneInfo.ConvertTime(instant,zone??TimeZoneInfo.Local).TimeOfDay;var from=QuietFrom.ToTimeSpan();var until=QuietUntil.ToTimeSpan();
  return from==until||(from<until?time>=from&&time<until:time>=from||time<until);}
 public LocalNotice? Record(string identity,string project,string? workId,string? decisionId,string kind,string title,DateTimeOffset? at=null)
 {if(string.IsNullOrWhiteSpace(identity)||string.IsNullOrWhiteSpace(project))throw new ArgumentException("Event identity and project are required.");
  lock(gate){if(store.Notices.Any(x=>x.Identity==identity&&string.Equals(x.Project,project,StringComparison.OrdinalIgnoreCase)))return null;
   var instant=at??DateTimeOffset.UtcNow;title=title.Replace('\r',' ').Replace('\n',' ').Trim();if(title.Length>100)title=title[..100];if(title.Length==0)title="Task status changed";
   var notice=new LocalNotice(identity,project,workId,decisionId,kind,title,instant,!IsQuiet(instant));store.Notices.Add(notice);
   if(store.Notices.Count>500)store.Notices.RemoveRange(0,store.Notices.Count-500);
   Save();return notice;}}
 private void Save(){Directory.CreateDirectory(Path.GetDirectoryName(path)!);var next=path+".next";
   using(var file=new FileStream(next,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,store,JsonFormat.Options);file.Flush(true);}File.Move(next,path,true);}
}
public sealed class ProjectNotificationWatcher:IDisposable
{
 private readonly NotificationCenter center;private readonly Action<LocalNotice> deliver;private readonly object gate=new();
 private FileSystemWatcher? watcher;private Timer? debounce;private string? project,directory;private readonly Dictionary<string,WorkStatus> work=new();private readonly HashSet<string> pending=[];private bool disposed;private DateTimeOffset firstChange;
 public ProjectNotificationWatcher(NotificationCenter center,Action<LocalNotice> deliver){this.center=center;this.deliver=deliver;}
 public void Select(string? project,string? dataRoot)
 {lock(gate){watcher?.Dispose();watcher=null;debounce?.Dispose();debounce=null;this.project=null;directory=null;work.Clear();pending.Clear();
  if(string.IsNullOrWhiteSpace(project)||!Directory.Exists(project))return;
  var full=Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..24];
  var dir=Path.Combine(dataRoot??RememberedRoot(hash)??ProjectRuntime.DefaultDataRoot,hash);if(!Directory.Exists(dir))return;
  this.project=full;directory=dir;var initial=ReadState(Path.Combine(dir,"state.json"));if(initial!=null)Prime(initial);
  watcher=new FileSystemWatcher(dir){Filter="*.json",NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.CreationTime};
  watcher.Changed+=Changed;watcher.Created+=Changed;watcher.Renamed+=Renamed;watcher.EnableRaisingEvents=true;ThreadPool.QueueUserWorkItem(_=>Scan());}}
 private static string? RememberedRoot(string hash)
 {try{var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","project-locations",hash+".json");using var doc=JsonDocument.Parse(File.ReadAllText(path));return doc.RootElement.GetProperty("DataRoot").GetString();}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException){return null;}}
 private void Renamed(object sender,RenamedEventArgs e)=>Changed(sender,e);
 private void Changed(object sender,FileSystemEventArgs e)
 {if(e.Name is not ("state.json" or "interaction-journal.json"))return;lock(gate)if(!disposed){if(debounce==null)firstChange=DateTimeOffset.UtcNow;debounce?.Dispose();var delay=DateTimeOffset.UtcNow-firstChange>=TimeSpan.FromSeconds(2)?1:180;debounce=new Timer(_=>Scan(),null,delay,Timeout.Infinite);}}
 public void Scan()
 {List<LocalNotice> outgoing=[];lock(gate){debounce?.Dispose();debounce=null;if(disposed||directory==null||project==null)return;
  var state=ReadState(Path.Combine(directory,"state.json"));if(state==null||!string.Equals(state.ProjectPath,project,StringComparison.OrdinalIgnoreCase))return;
  foreach(var item in state.Work){if(work.TryGetValue(item.Id,out var old)&&old!=item.Status&&item.Status is WorkStatus.Completed or WorkStatus.Failed or WorkStatus.Waiting or WorkStatus.Private)
   {var notice=center.Record($"work:{item.Id}:{item.Status}:{item.UpdatedAt.UtcTicks}",project,item.Id,null,item.Status.ToString(),string.IsNullOrWhiteSpace(item.Title)?"Task status changed":item.Title,item.UpdatedAt);if(notice?.Delivered==true)outgoing.Add(notice);}work[item.Id]=item.Status;}
  foreach(var decision in state.Decisions.Where(x=>x.Status==DecisionStatus.Pending)){if(!pending.Add(decision.Id))continue;
   var notice=center.Record("decision:"+decision.Id,project,decision.WorkId,decision.Id,"Decision","A task needs a decision",decision.CreatedAt);if(notice?.Delivered==true)outgoing.Add(notice);}
  // Opaque interaction journal content cannot create a notice; future typed producers call Record with identity.
 }foreach(var notice in outgoing)deliver(notice);}
 private void Prime(ProjectState state){foreach(var item in state.Work)work[item.Id]=item.Status;foreach(var decision in state.Decisions.Where(x=>x.Status==DecisionStatus.Pending))pending.Add(decision.Id);}
 private static ProjectState? ReadState(string path)
 {for(var i=0;i<5;i++)try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return JsonSerializer.Deserialize<ProjectState>(file,JsonFormat.Options);}
  catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}catch(IOException){Thread.Sleep(40);}catch(UnauthorizedAccessException){Thread.Sleep(40);}catch(JsonException){Thread.Sleep(40);}return null;}
 public void Dispose(){lock(gate){disposed=true;watcher?.Dispose();debounce?.Dispose();}}
}



