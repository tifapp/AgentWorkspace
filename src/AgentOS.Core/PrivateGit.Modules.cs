using System.Security.Cryptography;
using System.Text;
namespace AgentOS.Core;
internal static partial class PrivateGit
{
 public static async Task<List<ModulePin>> PrepareModules(string source,string destination,string commit,IReadOnlyList<ModulePin>? established=null)
 {
  var result=new List<ModulePin>();foreach(var entry in (await Entries(source,commit)).Where(x=>x.Mode=="160000"))
  {
   var prior=established?.FirstOrDefault(x=>x.Path==entry.Path);if(established!=null&&prior==null)throw new IOException("New module needs explicit source authority; remote .gitmodules URLs are not used.");
   var module=prior?.SourcePath??Path.Combine(source,entry.Path.Replace('/',Path.DirectorySeparatorChar));if(!Directory.Exists(module))throw new IOException("Trusted local module checkout missing.");
   var profile=await GitProjectProfile.Inspect(module);if(prior==null)
   {
    var owner=await GitProjectProfile.Inspect(source);if(!profile.SourcePath.StartsWith(owner.SourcePath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!(profile.Kind=="linked"&&profile.CommonDirectory.StartsWith(Path.Combine(owner.CommonDirectory,"modules")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||profile.Kind=="main"&&GitProjectProfile.SamePath(profile.CommonDirectory,Path.Combine(module,".git"))))throw new IOException("Module pointer leaves parent topology.");
   }
   else if(prior.SourceCommonIdentity!=profile.CommonIdentity||prior.SourceObjectIdentity!=profile.ObjectIdentity)throw new IOException("Pinned module identity changed.");
   if((await Commands.Git(module,"cat-file","-t",entry.Object)).Checked()!="commit")throw new IOException("Pinned module commit unavailable.");await VerifyTree(module,entry.Object);
   var target=Path.Combine(destination,entry.Path.Replace('/',Path.DirectorySeparatorChar));if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())throw new IOException("Module destination not empty.");
   Directory.CreateDirectory(Path.GetDirectoryName(target)!);(await Commands.Git(destination,"clone","--no-recurse-submodules","--no-hardlinks","--no-checkout","--",module,target)).Checked();
   (await Commands.Git(target,"fetch","--no-recurse-submodules","--no-tags","--no-write-fetch-head",module,entry.Object)).Checked();await Checkout(target,entry.Object);
   (await Commands.Git(target,"remote","remove","origin")).Checked();Prepare(target);await VerifySnapshot(target,entry.Object,await GitProjectProfile.Inspect(target));
   result.Add(new(entry.Path,entry.Object,profile.SourcePath,profile.CommonIdentity,profile.ObjectIdentity));
  }return result;
 }
 public static async Task<List<ModulePin>> CandidatePins(string workspace,string commit,IReadOnlyList<ModulePin> established)
 {
  var result=new List<ModulePin>();foreach(var entry in (await Entries(workspace,commit)).Where(x=>x.Mode=="160000")){var prior=established.FirstOrDefault(x=>x.Path==entry.Path)??throw new IOException("New module needs explicit source authority; remote .gitmodules URLs are not used.");result.Add(prior with{Commit=entry.Object});}return result;
 }
 public static async Task RetainModuleCandidates(string workspace,string candidate,IReadOnlyList<ModulePin> established,string workId)
 {
  foreach(var pin in await CandidatePins(workspace,candidate,established))
  {
   var profile=await GitProjectProfile.Inspect(pin.SourcePath);if(profile.CommonIdentity!=pin.SourceCommonIdentity||profile.ObjectIdentity!=pin.SourceObjectIdentity)throw new IOException("Module identity changed.");
   var privateModule=Path.Combine(workspace,pin.Path.Replace('/',Path.DirectorySeparatorChar));if((await Commands.Git(privateModule,"cat-file","-t",pin.Commit)).Checked()!="commit")throw new IOException("Private module commit unavailable.");
   var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin.Path)))[..16];(await Commands.Git(pin.SourcePath,"fetch","--no-recurse-submodules","--no-tags","--no-write-fetch-head",privateModule,pin.Commit+":refs/agent-os/candidates/"+workId+"/"+hash)).Checked();
  }
 }
 public static async Task RevalidateModules(IEnumerable<ModulePin> modules)
 {foreach(var pin in modules){var profile=await GitProjectProfile.Inspect(pin.SourcePath);if(profile.CommonIdentity!=pin.SourceCommonIdentity||profile.ObjectIdentity!=pin.SourceObjectIdentity||(await Commands.Git(pin.SourcePath,"cat-file","-t",pin.Commit)).Checked()!="commit")throw new IOException("Pinned module identity or commit changed.");}}
 public static async Task<CommandResult> ModuleOperation(string parentWorkspace,string relativeModule,string operation,string? message=null)
 {
  var root=SafePaths.Project(parentWorkspace);if(string.IsNullOrWhiteSpace(relativeModule)||Path.IsPathFullyQualified(relativeModule)||relativeModule.Split('/','\\').Any(x=>x is "." or ".."||x.Equals(".git",StringComparison.OrdinalIgnoreCase)))throw new IOException("Private module path unconfined.");
  var target=Path.GetFullPath(Path.Combine(root,relativeModule.Replace('/',Path.DirectorySeparatorChar)));if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Private module escapes workspace.");
  var stage=(await Commands.Git(root,"ls-files","--stage","--",relativeModule)).Checked();var tab=stage.IndexOf('\t');if(!stage.StartsWith("160000 ",StringComparison.Ordinal)||tab<0||!string.Equals(stage[(tab+1)..],relativeModule.Replace('\\','/'),StringComparison.Ordinal))throw new IOException("Path is not exact pinned module.");
  var profile=await GitProjectProfile.Inspect(target);if(profile.Kind!="main"||!GitProjectProfile.SamePath(profile.CommonDirectory,Path.Combine(target,".git")))throw new IOException("Private module is not isolated.");
  Prepare(target);return operation switch{"status"=>await Commands.Git(target,"status","--short","--untracked-files=all"),"diff"=>await Commands.Git(target,"diff","--no-ext-diff","--no-textconv"),"add"=>await Commands.Git(target,"add","-A","--","."),"commit" when !string.IsNullOrWhiteSpace(message)=>await Commands.Git(target,"-c","user.name=agent-os","-c","user.email=agent-os@localhost","commit","-m",message),"log"=>await Commands.Git(target,"log","-n","20","--oneline"),_=>throw new ArgumentException("Unsupported private module operation.")};
 } public static async Task<bool> ModulePinsChanged(string project,string before,string after)
 {var old=(await Entries(project,before)).Where(x=>x.Mode=="160000").ToDictionary(x=>x.Path,x=>x.Object,StringComparer.Ordinal);var next=(await Entries(project,after)).Where(x=>x.Mode=="160000").ToDictionary(x=>x.Path,x=>x.Object,StringComparer.Ordinal);return old.Count!=next.Count||next.Any(x=>!old.TryGetValue(x.Key,out var previous)||previous!=x.Value);}
}
