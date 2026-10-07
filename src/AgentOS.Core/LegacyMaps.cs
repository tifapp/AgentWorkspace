using System.Security.Cryptography;
using System.Text;
namespace AgentOS.Core;
internal static class LegacyMaps
{
 public static void Synthesize(ProjectState state)
 {
  state.Maps ??= []; if(state.Maps.Count!=0||state.Work.Count==0)return;
  var byId=state.Work.ToDictionary(w=>w.Id,StringComparer.Ordinal);
  string Root(WorkUnit w){var seen=new HashSet<string>(StringComparer.Ordinal){w.Id};while(w.ParentId!=null&&byId.TryGetValue(w.ParentId,out var parent)&&seen.Add(parent.Id))w=parent;return w.Id;}
  foreach(var group in state.Work.GroupBy(Root).OrderBy(g=>g.Min(w=>w.CreatedAt)))
  {
   var first=group.FirstOrDefault(w=>w.Id==group.Key)??group.First();var attempts=group.OrderBy(w=>w.CreatedAt).ThenBy(w=>w.Id,StringComparer.Ordinal).ToList();
   var title=string.IsNullOrWhiteSpace(first.Title)?string.IsNullOrWhiteSpace(first.Task)?"Historical task "+first.Id:first.ShortTask:first.Title;
   var task=new MapTask{Id=Stable("task:"+group.Key),Title=title,Prompt=string.IsNullOrWhiteSpace(first.Task)?"Historical work "+first.Id:first.Task,Acceptance="Preserve recorded historical work and evidence.",WorkId=first.Id,WorkIds=attempts.Select(w=>w.Id).ToList(),Status=Historical(attempts.Last().Status)};
   state.Maps.Add(new TaskMap{Id=Stable("map:"+group.Key),Title=title,ProjectPath=state.ProjectPath,Tasks=[task],CreatedAt=first.CreatedAt});
  }
 }
 public static void RestoreStatuses(ProjectState state)
 {
  var historical=state.HistoricalWorkIds.ToHashSet(StringComparer.Ordinal);
  foreach(var map in state.Maps.Where(m=>m.Status==MapStatus.Draft))foreach(var task in map.Tasks.Where(t=>t.WorkIds.Count>0&&t.WorkIds.All(historical.Contains)))
  {var latest=state.Work.Where(w=>task.WorkIds.Contains(w.Id)).OrderBy(w=>w.CreatedAt).ThenBy(w=>w.Id,StringComparer.Ordinal).LastOrDefault();if(latest!=null)task.Status=Historical(latest.Status);}
 }
 private static MapTaskStatus Historical(WorkStatus status)=>status switch{WorkStatus.Completed=>MapTaskStatus.Completed,WorkStatus.Private=>MapTaskStatus.Private,WorkStatus.Waiting=>MapTaskStatus.Waiting,WorkStatus.Validating=>MapTaskStatus.Validating,WorkStatus.Stale=>MapTaskStatus.Stale,WorkStatus.Failed=>MapTaskStatus.Failed,WorkStatus.Canceled=>MapTaskStatus.Canceled,WorkStatus.Unknown=>MapTaskStatus.Unknown,WorkStatus.NeedsResponse=>MapTaskStatus.NeedsResponse,WorkStatus.Parked=>MapTaskStatus.Parked,WorkStatus.Abandoned=>MapTaskStatus.Abandoned,_=>MapTaskStatus.Running};
 private static string Stable(string value)=>new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16)).ToString("N");
}
