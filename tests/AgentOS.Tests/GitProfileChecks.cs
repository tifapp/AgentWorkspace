using System.Runtime.InteropServices;
using AgentOS.Core;
namespace AgentOS.Tests;
internal static class GitProfileChecks
{
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 public static async Task RunAsync(string artifactRoot)
 {
  Directory.CreateDirectory(artifactRoot);var project=await PracticeProject.CreateAsync(artifactRoot);var profile=await GitProjectProfile.Inspect(project);
  Check(profile.Kind=="main"&&Directory.Exists(profile.CommonDirectory),"Main checkout profile missing.");
  var head=(await Commands.Git(project,"rev-parse","HEAD")).Checked();await PrivateGit.VerifySnapshot(project,head,profile);
  var linked=Path.Combine(artifactRoot,"linked");(await Commands.Git(project,"worktree","add","--detach",linked,head)).Checked();
  var linkedProfile=await GitProjectProfile.Inspect(linked);Check(linkedProfile.Kind=="linked"&&linkedProfile.CommonIdentity==profile.CommonIdentity,"Linked worktree identity changed.");
  var admission=new MachineCoordinator(Path.Combine(artifactRoot,"admission"));using var registration=admission.RegisterRuntime();using(var held=await admission.EnterAsync([profile.PublicationClaim],"main",null,CancellationToken.None)){using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(350));var blocked=false;try{using var rival=await admission.EnterAsync([linkedProfile.PublicationClaim],"linked",null,timeout.Token);}catch(OperationCanceledException){blocked=true;}Check(blocked,"Linked alias bypassed typed admission.");}
  var bare=Path.Combine(artifactRoot,"bare.git");(await Commands.Git(artifactRoot,"clone","--bare",project,bare)).Checked();
  var bareProfile=await GitProjectProfile.Inspect(bare);Check(bareProfile.Kind=="bare","Bare checkout profile missing.");await using(var opened=await ProjectRuntime.OpenInternal(bare,Path.Combine(artifactRoot,"bare-state"),new global::ScriptHost(),Path.Combine(artifactRoot,"bare-coordination")))Check(opened.Snapshot.SourceCommonIdentity==bareProfile.CommonIdentity,"Bare runtime identity was not persisted.");
  var module=await PracticeProject.CreateAsync(Path.Combine(artifactRoot,"module-root"));
  (await Commands.Git(project,"-c","protocol.file.allow=always","submodule","add",module,"child")).Checked();
  (await Commands.Git(project,"-c","user.name=test","-c","user.email=test@localhost","commit","-am","module")).Checked();
  var moduleProfile=await GitProjectProfile.Inspect(Path.Combine(project,"child"));
  Check(moduleProfile.Kind=="linked"&&moduleProfile.CommonDirectory.StartsWith(Path.Combine(profile.CommonDirectory,"modules"),StringComparison.OrdinalIgnoreCase),"Valid submodule pointer refused.");
  head=(await Commands.Git(project,"rev-parse","HEAD")).Checked();await PrivateGit.VerifySnapshot(project,head,await GitProjectProfile.Inspect(project));
  var workspace=Path.Combine(artifactRoot,"private");(await Commands.Git(artifactRoot,"clone","--no-recurse-submodules","--no-hardlinks","--no-checkout",project,workspace)).Checked();
  await PrivateGit.Checkout(workspace,head);var pins=await PrivateGit.PrepareModules(project,workspace,head);Check(pins.Count==1&&pins[0].Path=="child","Pinned local module was not prepared.");
  var denied=false;try{await PrivateGit.ModuleOperation(workspace,"../module-root","status");}catch(IOException){denied=true;}Check(denied,"Module path escape was admitted.");
  Check(GitProjectProfile.Identity(Path.Combine(workspace,"README.md"))!=GitProjectProfile.Identity(Path.Combine(project,"README.md")),"Private clone retained source hardlink.");
  var child=Path.Combine(workspace,"child");File.WriteAllText(Path.Combine(child,"candidate.txt"),"candidate");(await PrivateGit.ModuleOperation(workspace,"child","add")).Checked();(await PrivateGit.ModuleOperation(workspace,"child","commit","candidate")).Checked();
  (await Commands.Git(workspace,"add","-A")).Checked();var tree=(await Commands.Git(workspace,"write-tree")).Checked();var candidate=(await Commands.Git(workspace,"-c","user.name=test","-c","user.email=test@localhost","commit-tree",tree,"-p",head,"-m","candidate")).Checked();
  var changedPins=await PrivateGit.CandidatePins(workspace,candidate,pins);Check(changedPins.Count==1&&changedPins[0].Commit!=pins[0].Commit&&await PrivateGit.ModulePinsChanged(workspace,head,candidate),"Modified module pin was not bound to candidate.");await PrivateGit.RetainModuleCandidates(workspace,candidate,pins,"fixture");
  Check((await Commands.Git(Path.Combine(project,"child"),"cat-file","-t",changedPins[0].Commit)).Checked()=="commit","Module candidate was not retained.");Check((await Commands.Git(project,"rev-parse","HEAD")).Checked()==head,"Source checkout moved.");
  (await Commands.Git(project,"fetch","--no-tags","--no-write-fetch-head",workspace,candidate+":refs/agent-os/candidates/fixture")).Checked();
  var missingPin=false;try{await PrivateGit.PrepareModules(project,Path.Combine(artifactRoot,"untrusted-combined"),candidate,[]);}catch(IOException){missingPin=true;}Check(missingPin,"Combined candidate admitted an unapproved module pin.");
  var combined=Path.Combine(artifactRoot,"combined");(await Commands.Git(artifactRoot,"clone","--no-recurse-submodules","--no-hardlinks","--no-checkout",workspace,combined)).Checked();
  (await Commands.Git(combined,"fetch","--no-tags","--no-write-fetch-head",workspace,candidate)).Checked();await PrivateGit.Checkout(combined,candidate);var combinedPins=await PrivateGit.PrepareModules(project,combined,candidate,changedPins);
  Check(combinedPins.Count==1&&combinedPins[0].Commit==changedPins[0].Commit,"Combined-base validation did not materialize exact module pin.");  await LinkChecks(Path.Combine(artifactRoot,"links"));var called=false;GitNetworkPreconditions.Verify(artifactRoot,new Probe(()=>called=true));Check(called,"Network lock probe was not called.");var networkDenied=false;try{GitNetworkPreconditions.Verify(artifactRoot,new Probe(()=>throw new IOException("unsupported SMB lock")));}catch(IOException){networkDenied=true;}Check(networkDenied,"Failed network lock probe was admitted.");Console.WriteLine("SKIP live SMB publication: no configured test share.");
  var stateRoot=Path.Combine(artifactRoot,"legacy-state");Directory.CreateDirectory(stateRoot);var store=new StateStore(stateRoot);
  var original=new WorkUnit{Task="historical",Status=WorkStatus.Completed};var retry=new WorkUnit{Task="This is a revision of earlier work.",ParentId=original.Id,Status=WorkStatus.Stale};
  var raw=System.Text.Json.JsonSerializer.Serialize(new ProjectState{Schema=1,ProjectPath=project,Work=[original,retry]},JsonFormat.Options);File.WriteAllText(store.StatePath,raw);
  var migrated=store.Read()!;Check(migrated.Schema==3&&migrated.HistoricalWorkIds.SequenceEqual([original.Id,retry.Id]),"Legacy work IDs lost during schema migration.");
  Check(File.ReadAllText(store.StatePath+".schema1.bak")==raw,"Legacy state backup changed.");
 }
 static async Task LinkChecks(string root)
 {
  var repo=await PracticeProject.CreateAsync(root);var first=Path.Combine(repo,"README.md");var alias=Path.Combine(root,"alias.txt");
  if(CreateHardLink(alias,first,IntPtr.Zero))Check(ResourceAdmission.Conflicts(ResourceAdmission.File(alias,ResourceAccess.Write),ResourceAdmission.File(first,ResourceAccess.Write)),"Hardlink alias bypassed admission.");else Console.WriteLine("SKIP hardlink fixture: link creation unavailable.");
  var link=Path.Combine(repo,"link.txt");try{File.CreateSymbolicLink(link,"README.md");}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Console.WriteLine("SKIP symlink fixture: Windows capability unavailable.");return;}
  (await Commands.Git(repo,"add","-A")).Checked();(await Commands.Git(repo,"-c","user.name=test","-c","user.email=test@localhost","commit","-m","internal link")).Checked();
  var head=(await Commands.Git(repo,"rev-parse","HEAD")).Checked();await PrivateGit.VerifySnapshot(repo,head,await GitProjectProfile.Inspect(repo));
  File.Delete(link);File.CreateSymbolicLink(link,@"..\outside.txt");(await Commands.Git(repo,"add","-A")).Checked();(await Commands.Git(repo,"-c","user.name=test","-c","user.email=test@localhost","commit","-m","escape link")).Checked();head=(await Commands.Git(repo,"rev-parse","HEAD")).Checked();
  var refused=false;try{await PrivateGit.VerifyTree(repo,head);}catch(IOException){refused=true;}Check(refused,"Escaping tracked link passed tree preflight.");
 }
 private sealed class Probe(Action check):IGitNetworkLockProbe{public void Verify(string path)=>check();}
 [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string name,string existing,IntPtr reserved);}
