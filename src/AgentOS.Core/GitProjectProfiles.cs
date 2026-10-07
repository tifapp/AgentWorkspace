using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace AgentOS.Core;
internal sealed record GitProjectProfile(string SourcePath,string CommonDirectory,string CommonIdentity,string ObjectIdentity,string Kind,bool Network)
{
 public string ResourceKey=>"git:"+ResourceAdmission.CurrentUserSid+":"+CommonIdentity+":"+ProjectRuntime.IntegratedRef;
 public ResourceClaim PublicationClaim=>new(Network?"git-network":"directory",ResourceKey.ToUpperInvariant(),ResourceAccess.Write,CommonDirectory.ToUpperInvariant(),CommonIdentity,ResourceAdmission.CurrentUserSid);
 public static async Task<GitProjectProfile> Inspect(string input,IGitNetworkLockProbe? probe=null)
 {
  var selected=FinalPath(input,true);Preflight(selected);
  var bare=(await Commands.Git(selected,"rev-parse","--is-bare-repository")).Checked()=="true";
  var source=bare?selected:FinalPath((await Commands.Git(selected,"rev-parse","--show-toplevel")).Checked(),true);
  if(!SamePath(selected,source)&&!selected.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Selected path escapes Git top level.");
  var rawCommon=Single((await Commands.Git(source,"rev-parse","--git-common-dir")).Checked());
  var common=FinalPath(Path.IsPathFullyQualified(rawCommon)?rawCommon:Path.Combine(source,rawCommon),true);
  var rawGitdir=Single((await Commands.Git(source,"rev-parse","--git-dir")).Checked());
  var gitdir=FinalPath(Path.IsPathFullyQualified(rawGitdir)?rawGitdir:Path.Combine(source,rawGitdir),true);
  var marker=Path.Combine(source,".git");var kind=bare?"bare":File.Exists(marker)?"linked":"main";
  if(bare&&(!SamePath(source,common)||File.Exists(marker)||Directory.Exists(marker)))throw new IOException("Bare Git topology changed.");
  if(kind=="main"&&!SamePath(FinalPath(marker,true),common))throw new IOException("Main Git storage changed.");
  if(kind=="linked")
  {
   CheckPointer(source,gitdir,common);
   if(!SamePath(gitdir,common)&&!gitdir.StartsWith(Path.Combine(common,"worktrees")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&!gitdir.StartsWith(Path.Combine(common,"modules")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Git pointer leaves common storage.");
  }
  RejectStorage(common);RejectConfig(Path.Combine(common,"config"),kind=="linked"&&SamePath(gitdir,common),source);RejectConfig(Path.Combine(gitdir,"config.worktree"),false);
  var objects=FinalPath(Path.Combine(common,"objects"),true);var network=IsNetworkPath(source)||IsNetworkPath(common);
  if(network)GitNetworkPreconditions.Verify(common,probe??new SmbLockProbe());
  var listed=(await Commands.Git(source,"worktree","list","--porcelain")).Checked();
  if(kind=="linked"&&!SamePath(gitdir,common)&&!listed.Split('\n').Where(x=>x.StartsWith("worktree ",StringComparison.Ordinal)).Any(x=>Directory.Exists(x[9..].TrimEnd('\r'))&&SamePath(FinalPath(x[9..].TrimEnd('\r'),true),source)))throw new IOException("Linked worktree is unregistered.");
  if(listed.Split('\n').Any(x=>x.TrimEnd('\r')=="branch "+ProjectRuntime.IntegratedRef))throw new IOException("Shared ref is checked out.");
  return new(source,common,Identity(common),Identity(objects),kind,network);
 }
 static string Single(string value){var lines=value.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);if(lines.Length!=1||lines[0].StartsWith("--"))throw new IOException("Git returned ambiguous path output.");return lines[0];}
 public async Task Revalidate(){var now=await Inspect(SourcePath);if(now.CommonIdentity!=CommonIdentity||now.ObjectIdentity!=ObjectIdentity||now.Kind!=Kind||!SamePath(now.CommonDirectory,CommonDirectory)||!SamePath(now.SourcePath,SourcePath))throw new IOException("Git source identity changed.");}
 static void Preflight(string selected)
 {
  for(var path=selected;path!=null;path=Path.GetDirectoryName(path))
  {
   var marker=Path.Combine(path,".git");
   if(Directory.Exists(marker)){if((new DirectoryInfo(marker).Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Git storage junction untrusted.");RejectStorage(marker);RejectConfig(Path.Combine(marker,"config"),false);return;}
   if(File.Exists(marker)){if((new FileInfo(marker).Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Git pointer link untrusted.");var text=File.ReadAllText(marker).Trim();if(!text.StartsWith("gitdir: ",StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid Git pointer.");var target=FinalPath(Path.IsPathFullyQualified(text[8..].Trim())?text[8..].Trim():Path.Combine(path,text[8..].Trim()),true);var parent=Path.GetDirectoryName(target)!;var common=Path.GetFileName(parent).Equals("worktrees",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(parent)!:target;RejectStorage(common);return;}
   if(Directory.Exists(Path.Combine(path,"objects"))&&File.Exists(Path.Combine(path,"HEAD"))){RejectStorage(path);RejectConfig(Path.Combine(path,"config"),false);return;}
  }
  throw new IOException("No verified Git storage found.");
 }
 static void CheckPointer(string source,string gitdir,string common)
 {
  var marker=Path.Combine(source,".git");var info=new FileInfo(marker);if(!info.Exists||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Git pointer link untrusted.");
  var text=File.ReadAllText(marker).Trim();if(!text.StartsWith("gitdir: ",StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid Git pointer.");var value=text[8..].Trim();var target=FinalPath(Path.IsPathFullyQualified(value)?value:Path.Combine(source,value),true);
  if(!SamePath(target,gitdir)||(!IsNetworkPath(source)&&IsNetworkPath(target)))throw new IOException("Git pointer changed or crosses network boundary.");
  var parent=Path.GetDirectoryName(target)!;var worktree=Path.GetFileName(parent).Equals("worktrees",StringComparison.OrdinalIgnoreCase)&&Directory.Exists(Path.Combine(Path.GetDirectoryName(parent)!,"objects"));
  var module=false;for(var p=parent;p!=null;p=Path.GetDirectoryName(p))if(Path.GetFileName(p).Equals("modules",StringComparison.OrdinalIgnoreCase)&&Directory.Exists(Path.Combine(Path.GetDirectoryName(p)!,"objects"))){module=true;break;}
  if(!worktree&&!module)throw new IOException("Git pointer outside verified topology.");
  var backlink=Path.Combine(target,"gitdir");if(worktree)
  {
   var backInfo=new FileInfo(backlink);if(!backInfo.Exists||(backInfo.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Git backlink link untrusted.");var back=File.ReadAllText(backlink).Trim();if(!SamePath(FinalPath(Path.IsPathFullyQualified(back)?back:Path.Combine(target,back),false),FinalPath(marker,false)))throw new IOException("Git backlink mismatch.");
  }
  else
  {
   var owner=Path.GetDirectoryName(source);while(owner!=null&&!Directory.Exists(Path.Combine(owner,".git"))&&!File.Exists(Path.Combine(owner,".git")))owner=Path.GetDirectoryName(owner);
   if(owner==null)throw new IOException("Module pointer lacks parent checkout.");var expected=Path.GetFullPath(Path.Combine(owner,".git","modules",Path.GetRelativePath(owner,source)));
   if(!SamePath(expected,target))throw new IOException("Module pointer leaves parent storage.");RejectConfig(Path.Combine(target,"config"),true,source);
  }
 }
 static void RejectStorage(string common)
 {
  var objects=Path.Combine(common,"objects");if(!Directory.Exists(objects)||(new DirectoryInfo(objects).Attributes&FileAttributes.ReparsePoint)!=0||File.Exists(Path.Combine(objects,"info","alternates"))||File.Exists(Path.Combine(common,"commondir")))throw new IOException("External Git object storage untrusted.");
  var pending=new Stack<DirectoryInfo>();pending.Push(new DirectoryInfo(objects));while(pending.Count>0)foreach(var entry in pending.Pop().EnumerateFileSystemInfos()){if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Git object storage link untrusted.");if(entry is DirectoryInfo child)pending.Push(child);}
 }
 static void RejectConfig(string path,bool allowModuleWorktree,string? source=null)
 {
  if(!File.Exists(path))return;var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked Git config untrusted.");var data=File.ReadAllText(path);
  if(System.Text.RegularExpressions.Regex.IsMatch(data,@"(?im)^\s*\[\s*(include|includeIf|filter|credential|alias|uploadpack|receive|diff|merge|http|url|protocol|gpg)\b")||System.Text.RegularExpressions.Regex.IsMatch(data,@"(?im)^\s*(hooksPath|sshCommand|askPass|alternateObjectDirectories|editor|pager)\s*=")||System.Text.RegularExpressions.Regex.IsMatch(data,@"(?im)^\s*fsmonitor\s*=\s*(?!false\s*$|0\s*$)\S"))throw new IOException("Executable or external Git config untrusted.");
  var worktree=System.Text.RegularExpressions.Regex.Matches(data,@"(?im)^\s*worktree\s*=\s*(.+?)\s*$");
  if(worktree.Count>0){if(!allowModuleWorktree||source==null||worktree.Count!=1)throw new IOException("Git worktree config untrusted.");var value=worktree[0].Groups[1].Value.Trim();var resolved=Path.GetFullPath(Path.IsPathFullyQualified(value)?value:Path.Combine(Path.GetDirectoryName(path)!,value));if(!SamePath(resolved,source))throw new IOException("Module worktree config changed.");}
 }
 public static string CanonicalFutureDirectory(string path){var missing=new Stack<string>();var cursor=Path.GetFullPath(path);while(!Directory.Exists(cursor)){var parent=Path.GetDirectoryName(cursor)??throw new IOException("No data root ancestor.");missing.Push(Path.GetFileName(cursor));cursor=parent;}var result=FinalPath(cursor,true);while(missing.Count>0)result=Path.Combine(result,missing.Pop());return result;}
 public static bool IsNetworkPath(string path){if(path.StartsWith(@"\\",StringComparison.Ordinal))return true;var root=Path.GetPathRoot(path);return root!=null&&new DriveInfo(root).DriveType==DriveType.Network;}
 public static bool SamePath(string a,string b)=>string.Equals(Path.TrimEndingDirectorySeparator(a),Path.TrimEndingDirectorySeparator(b),StringComparison.OrdinalIgnoreCase);
 public static string FinalPath(string path,bool directory)
 {
  if(!OperatingSystem.IsWindows())return Path.GetFullPath(path);using var handle=CreateFile(Path.GetFullPath(path),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());var buffer=new char[32768];var length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Length,0);if(length==0||length>=buffer.Length)throw new Win32Exception(Marshal.GetLastWin32Error());var result=new string(buffer,0,(int)length);if(result.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))result=@"\\"+result[8..];else if(result.StartsWith(@"\\?\",StringComparison.OrdinalIgnoreCase))result=result[4..];if(Directory.Exists(result)!=directory)throw new IOException("Path kind changed.");return Path.TrimEndingDirectorySeparator(result);
 }
 public static string Identity(string path){using var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var value))throw new Win32Exception(Marshal.GetLastWin32Error());if(value.IndexHigh==0&&value.IndexLow==0)throw new IOException("Filesystem lacks stable file identity.");return $"{value.Volume:X8}:{value.IndexHigh:X8}{value.IndexLow:X8}";}
 [StructLayout(LayoutKind.Sequential)]private struct FileIdentity{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,char[] path,uint size,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileIdentity value);
}
internal interface IGitNetworkLockProbe{void Verify(string commonDirectory);}
internal sealed class SmbLockProbe:IGitNetworkLockProbe
{
 public void Verify(string commonDirectory)
 {
  var path=Path.Combine(commonDirectory,"agent-os.smb-lock-"+Guid.NewGuid().ToString("N"));
  try{using var first=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);first.WriteByte(1);first.Flush(true);try{using var second=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);throw new IOException("SMB did not enforce exclusive locking.");}catch(IOException e)when((e.HResult&0xFFFF)is 32 or 33){}}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException){throw new IOException("SMB locking unavailable; network publication closed.",e);}finally{try{File.Delete(path);}catch(IOException){}}
 }
}
internal static class GitNetworkPreconditions{public static void Verify(string path,IGitNetworkLockProbe probe)=>probe.Verify(path);}
