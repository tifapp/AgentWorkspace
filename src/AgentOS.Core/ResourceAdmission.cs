using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
namespace AgentOS.Core;
public enum ResourceAccess { Read, Write }
public sealed record ResourceClaim(string Kind,string Key,ResourceAccess Access,string? Path=null,string? FileIdentity=null,string? UserSid=null);
public static class ResourceAdmission
{
 public static string CurrentUserSid=>WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Windows user identity unavailable.");
 public static ResourceClaim Directory(string path,ResourceAccess access) { var p=Canonical(path,true); return new("directory",p,access,p,Identity(p),CurrentUserSid); }
 public static ResourceClaim File(string path,ResourceAccess access) { var p=Canonical(path,false); return new("file",p,access,p,Identity(p),CurrentUserSid); }
 public static ResourceClaim Git(string worktree,string gitDirectory,string baseRef,ResourceAccess access) {
  if(string.IsNullOrWhiteSpace(baseRef)||baseRef.Contains("..",StringComparison.Ordinal)||baseRef.Any(char.IsControl)) throw new ArgumentException("Invalid Git ref.");
  var tree=Directory(worktree,access); var git=Directory(gitDirectory,access);
  if(System.IO.File.Exists(System.IO.Path.Combine(tree.Path!,".git"))) throw new NotSupportedException("Linked worktrees need verified gitdir integration.");
  RejectUnsupportedGitTree(tree.Path!);
  if(!string.Equals(git.Path,System.IO.Path.Combine(tree.Path!,".git"),StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("External or bare Git storage is not admitted.");
  return new("git",git.Key+"|"+baseRef.ToUpperInvariant(),access,git.Path,git.FileIdentity,CurrentUserSid);
 }
 private static void RejectUnsupportedGitTree(string root) {
  if(System.IO.File.Exists(System.IO.Path.Combine(root,".gitmodules"))) throw new NotSupportedException("Submodules require immutable pin integration.");
  var pending=new Stack<string>(); pending.Push(root);
  while(pending.Count>0) foreach(var entry in System.IO.Directory.EnumerateFileSystemEntries(pending.Pop())) {
   if((System.IO.File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0) throw new NotSupportedException("Git symbolic links and junctions require verified target integration.");
   if(System.IO.Directory.Exists(entry)) pending.Push(entry);
   else {
    using var h=System.IO.File.OpenHandle(entry,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    if(!GetFileInformationByHandle(h,out var info)||info.Links!=1) throw new NotSupportedException("Git hard links require verified identity integration.");
   }
  }
 }
 public static string Canonical(string path,bool directory) {
  if(!OperatingSystem.IsWindows()||!System.IO.Path.IsPathFullyQualified(path)) throw new IOException("A fully qualified Windows resource path is required.");
  var full=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
  if(full.StartsWith(@"\\",StringComparison.Ordinal)) throw new NotSupportedException("Network paths require a verified adapter.");
  var root=System.IO.Path.GetPathRoot(full)!; var current=root;
  foreach(var part in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries)) {
   current=System.IO.Path.Combine(current,part);
   if((System.IO.File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("Unresolved junction or symbolic link.");
  }
  if(directory!=System.IO.Directory.Exists(full)) throw new IOException("Resource kind mismatch.");
  return full.ToUpperInvariant();
 }
 private static string Identity(string path) {
  using var h=System.IO.Directory.Exists(path) ? OpenDirectory(path) : System.IO.File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
  if(!GetFileInformationByHandle(h,out var i)) throw new Win32Exception(Marshal.GetLastWin32Error());
  return $"{i.Volume:X8}:{i.IndexHigh:X8}{i.IndexLow:X8}";
 }
 public static void Revalidate(ResourceClaim claim) {
  if(claim.UserSid!=null&&claim.UserSid!=CurrentUserSid) throw new IOException("Resource user identity changed.");
  if(claim.Kind is "directory" or "file" or "git") {
   if(claim.Path==null || claim.FileIdentity==null) throw new IOException("Resource path identity is missing.");
   var current=claim.Kind=="file"?File(claim.Path,claim.Access):Directory(claim.Path,claim.Access);
   if(current.Path!=claim.Path || current.FileIdentity!=claim.FileIdentity) throw new IOException("Resource identity changed while queued.");
  }
  if(claim.Kind=="process") {
   var parts=claim.Key.Split(':');
   if(parts.Length!=2 || !int.TryParse(parts[0],out var pid) || !long.TryParse(parts[1],out var ticks)) throw new IOException("Invalid process identity.");
   using var owner=VerifyProcess(pid,ticks);
  }
 }
 public static bool Conflicts(ResourceClaim a,ResourceClaim b) {
  if(a.UserSid!=null&&b.UserSid!=null&&a.UserSid!=b.UserSid) return false;
  if(a.Access==ResourceAccess.Read&&b.Access==ResourceAccess.Read) return false;
  if(a.Kind=="legacy"||b.Kind=="legacy") return a.Key==b.Key;
  if(a.FileIdentity!=null&&a.FileIdentity==b.FileIdentity) return true;
  if(a.Path!=null&&b.Path!=null) return a.Path==b.Path||
   (a.Kind=="directory"&&b.Path.StartsWith(a.Path+"\\",StringComparison.OrdinalIgnoreCase))||
   (b.Kind=="directory"&&a.Path.StartsWith(b.Path+"\\",StringComparison.OrdinalIgnoreCase));
  return a.Kind==b.Kind&&a.Key==b.Key;
 }
 public static BoundPort BindLoopbackPort(int port) {
  var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
  try { socket.ExclusiveAddressUse=true;socket.Bind(new IPEndPoint(IPAddress.Loopback,port));socket.Listen(1);return new(socket); }
  catch {socket.Dispose();throw;}
 }
 public static VerifiedProcess VerifyProcess(int pid,long ticks) {
  var p=Process.GetProcessById(pid);
  if(p.HasExited||p.StartTime.ToUniversalTime().Ticks!=ticks){p.Dispose();throw new IOException("Process identity changed.");}
  return new(p,ticks);
 }
 private static SafeFileHandle OpenDirectory(string path) { var h=CreateFile(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero); if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); return h; }
 [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle h,out FileInfo info);
}
public sealed class BoundPort(Socket socket):IDisposable {
 public int Port=>((IPEndPoint)socket.LocalEndPoint!).Port;
 public ResourceClaim Claim(ResourceAccess access=ResourceAccess.Write)=>new("port","127.0.0.1:"+Port,access,UserSid:ResourceAdmission.CurrentUserSid);
 public void Dispose()=>socket.Dispose();
}
public sealed class VerifiedProcess(Process process,long ticks):IDisposable {
 public ResourceClaim Claim(ResourceAccess access=ResourceAccess.Write)=>new("process",process.Id+":"+ticks,access,UserSid:ResourceAdmission.CurrentUserSid);
 public void StopOwned(){if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==ticks)process.Kill(true);}
 public void Dispose()=>process.Dispose();
}







// Inspection records do not authorize a Git mutation. Existing project policy must still admit the snapshot.
public sealed record GitSnapshotIdentity(ResourceClaim Worktree,ResourceClaim GitDirectory,string Commit,bool Integrated);
public static class GitSnapshotPreconditions
{
 public static GitSnapshotIdentity InspectLinked(string worktree,string immutableCommit,ResourceAccess access)
 {
  CheckCommit(immutableCommit);
  var tree=ResourceAdmission.Directory(worktree,access);
  var marker=Path.Combine(tree.Path!,".git");
  ResourceAdmission.File(marker,ResourceAccess.Read);
  var text=System.IO.File.ReadAllText(marker).Trim();
  if(!text.StartsWith("gitdir: ",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Linked Git directory marker is invalid.");
  var target=text[8..].Trim();
  var gitPath=Path.IsPathFullyQualified(target)?target:Path.GetFullPath(Path.Combine(tree.Path!,target));
  var git=ResourceAdmission.Directory(gitPath,access);
  var backlink=Path.Combine(git.Path!,"gitdir");
  ResourceAdmission.File(backlink,ResourceAccess.Read);
  var back=System.IO.File.ReadAllText(backlink).Trim();
  var backPath=Path.IsPathFullyQualified(back)?back:Path.GetFullPath(Path.Combine(git.Path!,back));
  if(ResourceAdmission.Canonical(backPath,false)!=ResourceAdmission.Canonical(marker,false)) throw new InvalidDataException("Linked worktree backlink mismatch.");
  return new(tree,git,immutableCommit.ToUpperInvariant(),false);
 }
 public static GitSnapshotIdentity InspectBare(string gitDirectory,string immutableCommit,ResourceAccess access)
 {
  CheckCommit(immutableCommit);
  var git=ResourceAdmission.Directory(gitDirectory,access);
  ResourceAdmission.File(Path.Combine(git.Path!,"HEAD"),ResourceAccess.Read);
  ResourceAdmission.Directory(Path.Combine(git.Path!,"objects"),ResourceAccess.Read);
  return new(git,git,immutableCommit.ToUpperInvariant(),false);
 }
 public static async Task ValidateSubmodulePinAsync(string worktree,string commit,string relativePath,string pin)
 {
  CheckCommit(commit); CheckCommit(pin);
  var tree=ResourceAdmission.Directory(worktree,ResourceAccess.Read);
  var child=Within(tree.Path!,relativePath);
  if(!Directory.Exists(child)) throw new IOException("Submodule checkout is missing.");
  var gitRelative=relativePath.Replace(Path.DirectorySeparatorChar,'/');
  var listing=(await Commands.Git(tree.Path!,"ls-tree",commit,"--",gitRelative)).Checked().Trim();
  var expected="160000 commit "+pin.ToLowerInvariant()+"\t"+gitRelative;
  if(!string.Equals(listing,expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Submodule pin differs from immutable tree.");
  var head=(await Commands.Git(child,"rev-parse","--verify","HEAD^{commit}")).Checked().Trim();
  if(!string.Equals(head,pin,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Submodule checkout differs from pinned commit.");
 }
 public static string ValidateLinkTarget(string worktree,string relativeLink,string target)
 {
  var tree=ResourceAdmission.Directory(worktree,ResourceAccess.Read);
  var link=Within(tree.Path!,relativeLink);
  if(Path.IsPathFullyQualified(target)||target.StartsWith(@"\\",StringComparison.Ordinal)||target.Contains('\0')) throw new IOException("Link target is not confined.");
  var resolved=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!,target));
  if(!resolved.StartsWith(tree.Path!+"\\",StringComparison.OrdinalIgnoreCase)&&!string.Equals(resolved,tree.Path,StringComparison.OrdinalIgnoreCase)) throw new IOException("Link target escapes snapshot.");
  return ResourceAdmission.Canonical(resolved,Directory.Exists(resolved));
 }
 private static string Within(string root,string relative)
 {
  if(string.IsNullOrWhiteSpace(relative)||Path.IsPathFullyQualified(relative)) throw new IOException("Snapshot path must be relative.");
  var path=Path.GetFullPath(Path.Combine(root,relative));
  if(!path.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("Snapshot path escapes worktree.");
  return path;
 }
 private static void CheckCommit(string value)
 {
  if((value.Length!=40&&value.Length!=64)||value.Any(x=>!Uri.IsHexDigit(x))) throw new ArgumentException("An immutable Git object ID is required.");
 }
}



