using System.Security.Cryptography;
using System.Text;
namespace AgentOS.Core;
internal static partial class PrivateGit
{
 private static async Task<List<(string Mode,string Object,string Path)>> Entries(string cwd,string commit)
 {
  var listing=(await Commands.Git(cwd,"ls-tree","-r","-z",commit)).Checked();
  return listing.Split('\0',StringSplitOptions.RemoveEmptyEntries).Select(line=>{var tab=line.IndexOf('\t');if(tab<0)throw new IOException("Invalid Git tree entry.");var head=line[..tab].Split(' ');if(head.Length<3)throw new IOException("Invalid Git tree entry.");var path=line[(tab+1)..];if(path.Contains('\\')||path.Split('/').Any(UnsafeSegment))throw new IOException("Git tree path unsafe on Windows.");return(head[0],head[2],path);}).ToList();
 }
 private static bool UnsafeSegment(string value)
 {
  var stem=value.Split('.')[0];return value is "" or "." or ".."||value.Equals(".git",StringComparison.OrdinalIgnoreCase)||value.EndsWith(' ')||value.EndsWith('.')||value.IndexOfAny(Path.GetInvalidFileNameChars())>=0||stem.Equals("CON",StringComparison.OrdinalIgnoreCase)||stem.Equals("PRN",StringComparison.OrdinalIgnoreCase)||stem.Equals("AUX",StringComparison.OrdinalIgnoreCase)||stem.Equals("NUL",StringComparison.OrdinalIgnoreCase)||(stem.Length==4&&(stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&stem[3] is >= '1' and <= '9');
 }
 public static async Task VerifyTree(string source,string commit)
 {
  var entries=await Entries(source,commit);var paths=entries.Select(x=>x.Path).ToHashSet(StringComparer.Ordinal);
  foreach(var entry in entries.Where(x=>x.Mode=="120000")){var blob=(await Commands.Git(source,"cat-file","blob",entry.Object)).Checked();CheckTreeTarget(entry.Path,blob,paths);}
 }
 public static async Task VerifySnapshot(string source,string commit,GitProjectProfile profile)
 {
  var entries=await Entries(source,commit);var paths=entries.Select(x=>x.Path).ToHashSet(StringComparer.Ordinal);
  foreach(var entry in entries.Where(x=>x.Mode=="120000"))
  {
   var target=(await Commands.Git(source,"cat-file","blob",entry.Object)).Checked();CheckTreeTarget(entry.Path,target,paths);if(profile.Kind=="bare")continue;
   var physical=Path.Combine(source,entry.Path.Replace('/',Path.DirectorySeparatorChar));FileSystemInfo info=Directory.Exists(physical)?new DirectoryInfo(physical):new FileInfo(physical);
   if(info.LinkTarget==null)throw new IOException("Tracked symlink requires Windows symlink capability; checkout contains a plain file.");
   if(info.LinkTarget.Replace('\\','/')!=target.Replace('\\','/'))throw new IOException("Tracked symlink target changed.");CheckLink(source,physical,info.LinkTarget);
  }
  foreach(var entry in entries.Where(x=>x.Mode=="160000"))
  {
   if(profile.Kind=="bare")throw new IOException("Bare source lacks a trusted local pinned module checkout.");var module=Path.Combine(source,entry.Path.Replace('/',Path.DirectorySeparatorChar));if(!Directory.Exists(module))throw new IOException("Pinned module checkout missing.");
   var child=await GitProjectProfile.Inspect(module);if(!child.SourcePath.StartsWith(profile.SourcePath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!(child.Kind=="linked"&&child.CommonDirectory.StartsWith(Path.Combine(profile.CommonDirectory,"modules")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||child.Kind=="main"&&GitProjectProfile.SamePath(child.CommonDirectory,Path.Combine(module,".git"))))throw new IOException("Module leaves verified parent topology.");
   if((await Commands.Git(module,"cat-file","-t",entry.Object)).Checked()!="commit")throw new IOException("Pinned module commit unavailable.");await VerifyTree(module,entry.Object);
  }
 }
 private static void CheckTreeTarget(string path,string target,HashSet<string> paths)
 {
  if(Path.IsPathRooted(target)||target.StartsWith("/",StringComparison.Ordinal)||target.Contains('\0'))throw new IOException("Tracked symlink escapes snapshot.");
  var parts=new List<string>(path.Split('/')[..^1]);foreach(var part in target.Replace('\\','/').Split('/')){if(part is "" or ".")continue;if(part==".."){if(parts.Count==0)throw new IOException("Tracked symlink escapes snapshot.");parts.RemoveAt(parts.Count-1);}else if(UnsafeSegment(part))throw new IOException("Tracked symlink target unsafe on Windows.");else parts.Add(part);}
  var resolved=string.Join("/",parts);if(!paths.Contains(resolved)&&!paths.Any(x=>x.StartsWith(resolved+"/",StringComparison.Ordinal)))throw new IOException("Tracked symlink target missing from snapshot.");
 }
 private static void CheckLink(string root,string path,string target)
 {
  if(Path.IsPathRooted(target)||target.StartsWith(@"\\",StringComparison.Ordinal))throw new IOException("Symlink escapes snapshot.");
  var resolved=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,target));var prefix=Path.GetFullPath(root).TrimEnd('\\')+"\\";
  if(!resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||resolved.Split(Path.DirectorySeparatorChar).Any(x=>x.Equals(".git",StringComparison.OrdinalIgnoreCase)))throw new IOException("Symlink escapes snapshot or enters Git storage.");
  if(!File.Exists(resolved)&&!Directory.Exists(resolved))throw new IOException("Symlink target dangling.");var final=GitProjectProfile.FinalPath(resolved,Directory.Exists(resolved));if(!final.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||final.Split(Path.DirectorySeparatorChar).Any(x=>x.Equals(".git",StringComparison.OrdinalIgnoreCase)))throw new IOException("Symlink resolves outside snapshot.");
 }
}
