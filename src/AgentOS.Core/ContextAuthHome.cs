using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
namespace AgentOS.Core;
internal static class ContextAuthHome
{
 const string Prefix="agent-os-context-";
 internal sealed record Owner(int Pid,long Started,string Nonce);
 static string Root=>Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
 static bool SafePath(string home)
 {
  try{string full=Path.GetFullPath(home),name=Path.GetFileName(full);return Path.IsPathFullyQualified(home)&&Path.GetDirectoryName(full)==Root&&(File.GetAttributes(Root)&FileAttributes.ReparsePoint)==0&&name.StartsWith(Prefix,StringComparison.Ordinal)&&Guid.TryParseExact(name[Prefix.Length..],"N",out _)&&Directory.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)==0;}catch{return false;}
 }
 static Owner? ReadOwner(string home)
 {
  try
  {
   if(!SafePath(home))return null;
   if(OperatingSystem.IsWindows()){var acl=new DirectoryInfo(home).GetAccessControl();var user=WindowsIdentity.GetCurrent().User;if(user==null||!acl.AreAccessRulesProtected||!user.Equals(acl.GetOwner(typeof(SecurityIdentifier))))return null;}
   string path=Path.Combine(home,"owner.json");var info=new FileInfo(path);if(!info.Exists||info.Length<1||info.Length>512||(info.Attributes&FileAttributes.ReparsePoint)!=0)return null;
   using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length<1||file.Length>512)return null;var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);if(file.ReadByte()!=-1)return null;var owner=JsonSerializer.Deserialize<Owner>(bytes);
   return owner is {Pid:>0,Started:>0}&&owner.Nonce==Path.GetFileName(home)[Prefix.Length..]?owner:null;
  }catch{return null;}
 }
 static bool Alive(Owner owner)
 {
  try{using var process=Process.GetProcessById(owner.Pid);return process.StartTime.ToUniversalTime().Ticks==owner.Started;}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch{return true;}
 }
 static bool ChildrenSafe(string home)
 {
  try{string prefix=Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;var pending=new Stack<string>();pending.Push(home);int entries=0;while(pending.Count>0)foreach(var child in Directory.EnumerateFileSystemEntries(pending.Pop())){if(++entries>20000||!Path.GetFullPath(child).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||(File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)return false;if(Directory.Exists(child))pending.Push(child);}return true;}catch{return false;}
 }
 public static string Create()
 {
  string nonce=Guid.NewGuid().ToString("N"),home=Path.Combine(Root,Prefix+nonce);Directory.CreateDirectory(home);if(!SafePath(home))throw new IOException("Temporary auth path is unsafe.");
  if(OperatingSystem.IsWindows()){var user=WindowsIdentity.GetCurrent().User??throw new IOException("Current user unavailable.");var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(user);acl.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(home).SetAccessControl(acl);}
  using var self=Process.GetCurrentProcess();var owner=new Owner(self.Id,self.StartTime.ToUniversalTime().Ticks,nonce);using(var file=new FileStream(Path.Combine(home,"owner.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){JsonSerializer.Serialize(file,owner);file.Flush(true);}if(ReadOwner(home)!=owner)throw new IOException("Temporary auth owner could not be verified.");return home;
 }
 public static void Recover(){string[] homes;try{homes=Directory.EnumerateDirectories(Root,Prefix+"*").ToArray();}catch{return;}foreach(var home in homes)RecoverOwned(home);}
 internal static void RecoverOwned(string home){var owner=ReadOwner(home);if(owner!=null&&!Alive(owner)&&ChildrenSafe(home))try{Directory.Delete(home,true);}catch{}}
 public static void Cleanup(string home){var owner=ReadOwner(home);if(owner!=null&&owner.Pid==Environment.ProcessId&&Alive(owner)&&ChildrenSafe(home))try{Directory.Delete(home,true);}catch{}}
}
