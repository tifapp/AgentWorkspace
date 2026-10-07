using System.Collections.Concurrent;

namespace AgentOS.Core;

public sealed partial class ProjectRuntime : IAsyncDisposable
{
    public const string IntegratedRef = "refs/heads/agent-os/integrated";
    private readonly object _sync = new();
    private readonly StateStore _store;
    private readonly FileStream _projectLock;
    private readonly SemaphoreSlim _publication = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new();
    private readonly ConcurrentDictionary<string, Task> _jobs = new();
    private readonly IWorkHost _host;
    private readonly GitProjectProfile _profile;
    internal MachineCoordinator Coordinator { get; }
    private readonly IDisposable _runtimeRegistration;
    private readonly TaskInteractionStore _interactions;
    private ProjectState _state;
    private bool _disposed;
    private ProjectBroker? _broker;
    public string DataDirectory => _store.Root;
    public string CommonProjectKey => _profile.CommonIdentity;
    public event Action? Changed;
    public ProjectState Snapshot { get { lock (_sync) return JsonFormat.Copy(_state); } }
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "projects");
    public static string Coverage => "Codex CLI 0.160.0 on Windows uses private Git clones, AppContainer PowerShell, expected-old shared Git publication, validation evidence, task maps, scoped interactions, notifications, and loopback previews. Supported main, linked, and bare Git sources use canonical common-storage identity; SMB admission requires an actual lock proof, and unsupported topology or locking fails closed. Native SDK work requires an explicitly configured available Hyper-V profile with a separate nonadministrator guest worker and exact shutdown receipt; ordinary PowerShell remains in AppContainer. Live external effects require their own scoped configuration, review, and reconciliation. Production signed updates require signing and machine prerequisites. Phone features are deferred.";

    private ProjectRuntime(StateStore store, FileStream projectLock, ProjectState state, IWorkHost host, MachineCoordinator coordinator, IDisposable registration, GitProjectProfile profile)
    { _profile=profile;Coordinator = coordinator; _runtimeRegistration = registration; _store = store; _projectLock = projectLock; _state = state; _host = host; _interactions = new TaskInteractionStore(store.Root); RestoreUpdateDrain(); if (host is ManagedCodexHost managed) { managed.Interactions = _interactions; managed.Runtime = this; } }

    public static Task<ProjectRuntime> OpenAsync(string project,string? dataRoot=null)=>OpenInternal(project,dataRoot,new ManagedCodexHost(),null);
    internal static Task<ProjectRuntime> OpenAsync(string project,string? dataRoot,string coordinatorRoot)=>OpenInternal(project,dataRoot,new ManagedCodexHost(),coordinatorRoot);
    internal static async Task<ProjectRuntime> OpenInternal(string project, string? dataRoot, IWorkHost host, string? coordinatorRoot)
    {
        var profile=await GitProjectProfile.Inspect(project);
        project=profile.SourcePath;var gitdir=profile.CommonDirectory;
        FileStream projectLock;
        try { projectLock = new FileStream(Path.Combine(gitdir, "agent-os.runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("This project is already open in another agent-os runtime. Close that window before opening it here."); }
        try
        {
            var head=(await Commands.Git(project,"rev-parse","--verify","HEAD")).Checked();
            await PrivateGit.VerifySnapshot(project,head,profile);
            var reference=await Commands.Git(project,"rev-parse","--verify",IntegratedRef);
            dataRoot=GitProjectProfile.CanonicalFutureDirectory(dataRoot??ProjectLocations.Find(profile.CommonIdentity)??ProjectLocations.Find(profile.CommonDirectory)??ProjectLocations.Find(project)??DefaultDataRoot);
            if(GitProjectProfile.IsNetworkPath(dataRoot)||GitProjectProfile.SamePath(dataRoot,project)||dataRoot.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||GitProjectProfile.SamePath(dataRoot,gitdir)||dataRoot.StartsWith(gitdir+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Project state requires local storage outside source Git checkout.");
            Directory.CreateDirectory(dataRoot);dataRoot=GitProjectProfile.FinalPath(dataRoot,true);
            if(GitProjectProfile.IsNetworkPath(dataRoot))throw new IOException("Project state alias resolves to network storage.");
            var mainPath=Path.GetFileName(profile.CommonDirectory).Equals(".git",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(profile.CommonDirectory)!:profile.CommonDirectory;
            var folders=new[]{profile.CommonIdentity,profile.CommonDirectory,project,mainPath}.Select(key=>Path.Combine(dataRoot,StateStore.Key(key))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing=folders.Where(path=>File.Exists(Path.Combine(path,"state.json"))).ToList();if(existing.Count>1)throw new InvalidDataException("Multiple saved state folders claim this Git identity.");
            var store=new StateStore(existing.Count==1?existing[0]:folders[0]);var saved=store.Read();
            var state=saved??new ProjectState{ProjectPath=project,WorkMapMigrationComplete=true,IntegratedCommit=reference.ExitCode==0?reference.Checked():head,CodexPath=HostDiscovery.FindCodex()??""};
            if(saved!=null&&state.SourceCommonIdentity!=null&&(state.SourceCommonIdentity!=profile.CommonIdentity||state.SourceObjectIdentity!=profile.ObjectIdentity||(state.SourceCommonDirectory!=null&&!GitProjectProfile.SamePath(state.SourceCommonDirectory,profile.CommonDirectory))))throw new InvalidDataException("Saved Git object identity differs from opened source.");
            if(saved!=null&&state.SourceCommonIdentity==null&&!GitProjectProfile.SamePath(state.ProjectPath,project)&&!GitProjectProfile.SamePath(state.ProjectPath,mainPath))throw new InvalidDataException("Saved project identity does not match source.");
            state.SourceCommonIdentity=profile.CommonIdentity;state.SourceObjectIdentity=profile.ObjectIdentity;state.SourceCommonDirectory=profile.CommonDirectory;state.SourceKind=profile.Kind;state.ProjectPath=project;
            if(reference.ExitCode!=0)
            {
                if(saved!=null)throw new IOException("Saved shared Git ref missing; opening would reset history.");
                var initializer=new MachineCoordinator(coordinatorRoot);using var initializationRegistration=initializer.RegisterRuntime();using var admission=await initializer.EnterAsync(new[]{profile.PublicationClaim},"initialize shared ref",null,CancellationToken.None);
                await profile.Revalidate();await PrivateGit.VerifySnapshot(project,head,profile);
                if((await Commands.Git(project,"rev-parse","HEAD")).Checked()!=head||(await Commands.Git(project,"rev-parse","--verify",IntegratedRef)).ExitCode==0)throw new IOException("Source changed before shared ref initialization.");
                (await Commands.Git(project,"update-ref",IntegratedRef,head,new string('0',head.Length))).Checked();
            }
            var coordinator = new MachineCoordinator(coordinatorRoot);
            var registration = coordinator.RegisterRuntime();
            ProjectRuntime runtime;
            try { runtime = new ProjectRuntime(store, projectLock, state, host, coordinator, registration, profile); }
            catch { registration.Dispose(); throw; }
            try
            {
                await runtime.Recover();
                runtime._interactions.Recover(id => false);
                ProjectLocations.Remember(profile.CommonIdentity,dataRoot);
                ProjectLocations.Remember(profile.CommonDirectory,dataRoot);
                ProjectLocations.Remember(project, dataRoot);
                runtime._broker = new ProjectBroker(runtime);
                return runtime;
            }
            catch { await runtime.DisposeAsync(); throw; }
        }
        catch { projectLock.Dispose(); throw; }
    }

    private async Task Recover()
    {
        WorkExecution.ReconcileOwnership(_store.Root);
        ManagedSandbox.RecoverProfiles();
        await _profile.Revalidate();
        var current = (await Commands.Git(_state.ProjectPath, "rev-parse", IntegratedRef)).Checked();
        foreach(var owned in _state.Work.Where(x=>x.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,x.Id))){owned.SdkShutdownDebt=true;owned.Status=WorkStatus.Unknown;owned.Detail="SDK VM shutdown unproven after restart; publication and cleanup disabled.";Event(owned.Id,"Recovery",owned.Detail);}
        foreach (var work in _state.Work.Where(x => !_state.HistoricalWorkIds.Contains(x.Id) && !x.SdkShutdownDebt && (x.IsActive || x.PendingCommit != null || (x.Status == WorkStatus.Unknown && x.IntegratedCommit != null))))
        {
            var intended = work.PendingCommit ?? work.IntegratedCommit;
            if (intended != null)
            {
                var ancestor = await Commands.Git(_state.ProjectPath, "merge-base", "--is-ancestor", intended, current);
                if (ancestor.ExitCode == 0)
                {
                    work.Status = _state.Conflicts.Any(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned) ? WorkStatus.Parked : WorkStatus.Completed; work.IntegratedCommit = intended;
                    work.PendingCommit = null;
                    work.Detail = "Recovered a completed integration from Git. It was not repeated.";
                }
                else { work.Status = WorkStatus.Unknown; work.Detail = "Integration was interrupted. The intended commit is not in the current shared history. Inspect Git before retrying; no effect was replayed."; }
            }
            else { work.Status = WorkStatus.Unknown; work.Detail = "The runtime stopped before confirming completion. Owned process trees were closed; private files are retained. Start a revised task to continue."; }
            Event(work.Id, "Recovery", work.Detail);
        }
        foreach (var work in _state.Work.Where(x=>!_state.HistoricalWorkIds.Contains(x.Id)&&!x.SdkShutdownDebt))
        {
            var unresolved = _state.Conflicts.Where(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned).ToArray();
            if (unresolved.Length == 0 || work.Status is WorkStatus.NeedsResponse or WorkStatus.Parked) continue;
            work.Status = unresolved.Any(x => x.Response == null) ? WorkStatus.NeedsResponse : WorkStatus.Parked;
            work.Detail = "Recovered unresolved conflict and retained its private candidate. Resume the original thread.";
            Event(work.Id, "Recovery", work.Detail);
        }
        foreach (var decision in _state.Decisions.Where(x => !_state.HistoricalWorkIds.Contains(x.WorkId) && (x.Status is DecisionStatus.Approved or DecisionStatus.Unknown)))
        {
            var actual = await Commands.Git(_state.ProjectPath, "rev-parse", "--verify", decision.Destination);
            if (actual.ExitCode == 0 && actual.Checked() == decision.Candidate)
            { decision.Status = DecisionStatus.Completed; decision.Note = "Recovered the exact release tag from Git. No duplicate publication."; }
            else { decision.Status = DecisionStatus.Unknown; decision.Note = "The saved release intent has no confirmed matching tag. Inspect Git; automatic replay is disabled."; }
        }
        _state.IntegratedCommit = current;
        UpdateMapStatuses();
        Save();
    }

    public void Configure(string validationCommand, string? codexPath = null)
    {
        if (string.IsNullOrWhiteSpace(validationCommand)) throw new ArgumentException("Enter the project's validation command before starting work.");
        Mutate(() => { _state.ValidationCommand = validationCommand.Trim(); if (codexPath != null) _state.CodexPath = codexPath; });
    }

    public Task<string> StartAsync(string task, bool autoIntegrate = true, string? parentId = null, string? externalRequestId = null, IReadOnlyList<ContextArtifactRef>? contextRefs = null)
        => StartCore(task,autoIntegrate,parentId,externalRequestId,contextRefs,WorkRelationship.Unknown);
    private Task<string> StartCore(string task,bool autoIntegrate,string? parentId,string? externalRequestId,IReadOnlyList<ContextArtifactRef>? contextRefs,WorkRelationship relationship)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("Describe the work for Codex.");
        WorkUnit work;
        lock (_sync)
        {
            if (externalRequestId != null)
            {
                if (!Guid.TryParseExact(externalRequestId, "N", out _)) throw new ArgumentException("Task request identity is invalid.");
                var accepted = _state.Work.SingleOrDefault(x => x.ExternalRequestId == externalRequestId);
                if (accepted != null)
                {
                    if (accepted.Task != task.Trim() || !accepted.ContextRefs.SequenceEqual(contextRefs??[])) throw new ArgumentException("A task request identity cannot be reused for different work.");
                    return Task.FromResult(accepted.Id);
                }
            }
            RequireUpdateAdmission();
            if (string.IsNullOrWhiteSpace(_state.ValidationCommand)) throw new InvalidOperationException("Save a validation command in Project setup first.");
            WorkUnit? parent = null;
            if (parentId != null) parent = _state.Work.SingleOrDefault(x => x.Id == parentId)
                ?? throw new ArgumentException("The original task does not belong to this project.", nameof(parentId));
            if(contextRefs!=null)new ContextArtifacts(_store.Root).Verify(contextRefs);
            var execution=parent==null?WorkExecution.Capture(_store.Root):(Snapshot:parent.ExecutionProfileSnapshot,Hash:parent.ExecutionProfileSha256);
            if(parent!=null)WorkExecution.EnsureAvailable(parent);
            work = new WorkUnit { ExecutionProfileSnapshot=execution.Snapshot,ExecutionProfileSha256=execution.Hash,Task = task.Trim(), ParentId = parentId, Relationship = parentId==null?WorkRelationship.Original:relationship==WorkRelationship.Unknown?WorkRelationship.Followup:relationship, ContextRefs=contextRefs?.ToList()??[], AutoIntegrate = autoIntegrate, ValidationCommand = _state.ValidationCommand, ExternalRequestId = externalRequestId };
            work.Title = work.Relationship==WorkRelationship.Revision?parent?.ShortTask:null;
            work.Workspace = Path.Combine(_store.Root, "workspaces", work.Id);
            _state.Work.Add(work);
            UpdateMapStatuses();
            Event(work.Id, "Started", "Preparing a private Codex work unit.");
            Save();
        }
        var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tokens[work.Id] = token;
        _jobs[work.Id] = Task.Run(() => Execute(work, token.Token));
        Changed?.Invoke();
        return Task.FromResult(work.Id);
    }

    private async Task Execute(WorkUnit work, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var project = _state.ProjectPath;
            await _profile.Revalidate();work.SourceHead=(await Commands.Git(project,"rev-parse","HEAD")).Checked();
            await PrivateGit.VerifySnapshot(project,work.SourceHead,_profile);
            work.BaseCommit=(await Commands.Git(project,"rev-parse",IntegratedRef)).Checked();await PrivateGit.VerifyTree(project,work.BaseCommit);
            Directory.CreateDirectory(work.Workspace);
            using(var preparing=await Coordinator.EnterAsync(new[]{ResourceAdmission.Directory(work.Workspace,ResourceAccess.Write)},work.ShortTask,null,token))
            {
                (await Commands.Git(project,"clone","--no-recurse-submodules","--no-hardlinks","--no-checkout","--",project,work.Workspace)).Checked();
                await PrivateGit.Checkout(work.Workspace,work.BaseCommit);
                work.ModulePins=await PrivateGit.PrepareModules(project,work.Workspace,work.BaseCommit);
                await PrivateGit.VerifySnapshot(work.Workspace,work.BaseCommit,await GitProjectProfile.Inspect(work.Workspace));
                (await Commands.Git(work.Workspace,"remote","remove","origin")).Checked();PrivateGit.Prepare(work.Workspace);
            }
            token.ThrowIfCancellationRequested();
            Set(work, WorkStatus.Running, "Codex is working in a private clone. Shared files have not changed.");
            work.HostVersion = await _host.Version(_state.CodexPath);
            var result = await _host.Run(work, _state.CodexPath, LogPath(work, "codex.jsonl"), line =>
            {
                if (line.Length > 3000) line = line[..3000] + "...";
                Mutate(() => { Event(work.Id, "Codex", line); });
            }, token);
            Mutate(() => { work.ThreadId = result.ThreadId; work.CodexReport = result.Report; work.HostModel = result.Model; });
            if(work.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,work.Id)){MarkSdkUnknown(work.Id);return;}
            if (result.ExitCode != 0 || !result.TurnCompleted)
            {
                Set(work, result.ExitCode == 0 ? WorkStatus.Unknown : WorkStatus.Failed,
                    $"Codex exited with code {result.ExitCode}" + (result.TurnCompleted ? "." : " without a confirmed successful turn.") + " Private files and the transcript are retained.");
                return;
            }
            token.ThrowIfCancellationRequested();
            if (_host is ManagedCodexHost) return;
            await Capture(work,token);
            Set(work, WorkStatus.Private, $"Private candidate prepared: {work.ChangedPaths.Count} changed paths. Validation and integration are still required.");
            if (work.AutoIntegrate) await IntegrateCore(work, token);
        }
        catch (OperationCanceledException) { var pending = _state.Conflicts.Where(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned).ToArray(); Set(work, pending.Length == 0 ? WorkStatus.Canceled : pending.Any(x => x.Response == null) ? WorkStatus.NeedsResponse : WorkStatus.Parked, "Canceled. Owned processes stopped; private candidate and any unresolved conflict are retained."); }
        catch (Exception e) { RecordFailure(work, e); }
    }

    private Task Capture(WorkUnit work)=>Capture(work,_tokens.TryGetValue(work.Id,out var source)?source.Token:_lifetime.Token);
    private async Task Capture(WorkUnit work,CancellationToken token)
    {
        var claims=new List<ResourceClaim>{ResourceAdmission.Directory(work.Workspace,ResourceAccess.Write),_profile.PublicationClaim};claims.AddRange(await ModuleSourceClaims(work.ModulePins));
        using var admission=await Coordinator.EnterAsync(Coalesce(claims),work.ShortTask,null,token);
        PrivateGit.Prepare(work.Workspace);(await Commands.Git(work.Workspace,"add","-A","--",".")).Checked();
        var tree=(await Commands.Git(work.Workspace,"write-tree")).Checked();await PrivateGit.CandidatePins(work.Workspace,tree,work.ModulePins);
        await PrivateGit.VerifySnapshot(work.Workspace,tree,await GitProjectProfile.Inspect(work.Workspace));
        var candidate=await Commit(work.Workspace,tree,work.BaseCommit,"agent-os: "+work.ShortTask);
        work.CandidateModulePins=await PrivateGit.CandidatePins(work.Workspace,candidate,work.ModulePins);
        await PrivateGit.RetainModuleCandidates(work.Workspace,candidate,work.ModulePins,work.Id);
        await _profile.Revalidate();await CheckNetworkVersion(work);
        await PrivateGit.VerifySnapshot(_state.ProjectPath,(await Commands.Git(_state.ProjectPath,"rev-parse","HEAD")).Checked(),_profile);
        (await Commands.Git(_state.ProjectPath,"fetch","--no-recurse-submodules","--no-tags","--no-write-fetch-head",work.Workspace,"+"+candidate+":refs/agent-os/candidates/"+work.Id)).Checked();
        var paths=(await Commands.Git(work.Workspace,"diff","--name-only","--no-renames","-z",work.BaseCommit,candidate)).Checked().Split('\0',StringSplitOptions.RemoveEmptyEntries).ToList();
        var diff=(await Commands.Git(work.Workspace,"diff","--no-ext-diff","--no-textconv",work.BaseCommit,candidate)).Checked();
        Mutate(()=>{work.CandidateCommit=candidate;work.ChangedPaths=paths;work.Diff=diff;});
    }    private static async Task<string> Commit(string cwd, string tree, string parent, string message) =>
        (await Commands.Git(cwd, "-c", "user.name=agent-os", "-c", "user.email=agent-os@localhost", "commit-tree", tree, "-p", parent, "-m", message)).Checked();

    public Task IntegrateAsync(string id)
    {
        var work = Find(id);
        lock (_sync)
        {
            RequireUpdateAdmission();
            if (_state.Conflicts.Any(x => x.WorkId == id && !x.Resolved && !x.Abandoned)) throw new InvalidOperationException("Deferred conflict remains unresolved.");
            if (work.Status == WorkStatus.Completed) return Task.CompletedTask;
            if (work.Status != WorkStatus.Private) throw new InvalidOperationException("Only a prepared private candidate can be integrated. Revise stale or failed work first.");
            if (_jobs.TryGetValue(id, out var old) && !old.IsCompleted) throw new InvalidOperationException("This work unit is still finishing. Try again when it is idle.");
            var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _tokens[id] = token;
            return _jobs[id] = Task.Run(() => IntegrateGuarded(work, token.Token));
        }
    }
    private async Task IntegrateGuarded(WorkUnit work, CancellationToken token)
    {
        try { await IntegrateCore(work, token); }
        catch (OperationCanceledException) { Set(work, WorkStatus.Canceled, "Canceled before integration. Private candidate retained."); }
        catch (Exception e) { RecordFailure(work, e); }
    }

    private async Task IntegrateCore(WorkUnit work, CancellationToken token)
    {
        if(work.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,work.Id))throw new IOException("SDK VM shutdown unproven; publication forbidden.");
        Set(work, WorkStatus.Waiting, "Waiting to validate and integrate. Other Codex tasks can continue in their private workspaces.");
        await _publication.WaitAsync(token);
        try
        {
            var claims=new List<ResourceClaim>{_profile.PublicationClaim};claims.AddRange(await ModuleSourceClaims(work.CandidateModulePins));
            using var admission = await Coordinator.EnterAsync(Coalesce(claims), work.ShortTask,
                message => Mutate(() => Event(work.Id, "Coordination", message)), token);
            token.ThrowIfCancellationRequested();
            var project = _state.ProjectPath;
            await _profile.Revalidate();
            var current = (await Commands.Git(project, "rev-parse", IntegratedRef)).Checked();
            await PrivateGit.VerifyTree(project,current);
            var resolvedPaths = _state.Conflicts.Where(x => x.WorkId == work.Id && x.ResolutionRequested && !x.Resolved && !x.Abandoned).SelectMany(x => x.Paths).ToHashSet(StringComparer.Ordinal);
            // Check the complete path identity, including mode and blob, after ownership is acquired.
            var changedTouchedPaths = new List<string>();
            foreach (var path in work.ChangedPaths)
            {
                if (resolvedPaths.Contains(path)) continue;
                var before = (await Commands.Git(project, "ls-tree", "-z", work.BaseCommit, "--", path)).Checked();
                var now = (await Commands.Git(project, "ls-tree", "-z", current, "--", path)).Checked();
                if (before != now) changedTouchedPaths.Add(path);
            }
            if (changedTouchedPaths.Count > 0) { await RecordConflict(work, "TouchedPathChanged", changedTouchedPaths, current); return; }
            var index = Path.Combine(_store.Root, "integrate-" + Guid.NewGuid().ToString("N") + ".index");
            string tree;
            try
            {
                var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
                // All touched paths have matched their base identities. Apply their exact delta
                // to a private index of current; no fuzzy/three-way content merge is authorized.
                // This also handles directory/file replacements on older Git versions.
                (await Commands.RunAsync("git", ["read-tree", current], project, token, environment: env)).Checked();
                var patchPaths = work.ChangedPaths.Where(x => !resolvedPaths.Contains(x)).ToArray();
                var patch = await Commands.Git(project, (new[] { "diff", "--binary", "--full-index", "--no-renames", "--no-ext-diff", "--no-textconv", work.BaseCommit, work.CandidateCommit!, "--" }).Concat(patchPaths).ToArray());
                patch.Checked();
                var merged = patchPaths.Length == 0 ? new CommandResult(0, "", "") :
                    await Commands.RunAsync("git", ["apply", "--cached", "--binary", "--whitespace=nowarn"], project, token, input: patch.Output, environment: env);
                if (merged.ExitCode == 0)
                {
                    try { await StageResolvedPaths(work, resolvedPaths, env, token); }
                    catch (Exception e) when (e is IOException or InvalidDataException) { await RecordConflict(work, "ExactPatchFailure", work.ChangedPaths, current); return; }
                }
                var written = await Commands.RunAsync("git", ["write-tree"], project, token, environment: env);
                if (merged.ExitCode != 0 || written.ExitCode != 0) { await RecordConflict(work, "ExactPatchFailure", work.ChangedPaths, current); return; }
                tree = written.Checked();
            }
            finally { if (File.Exists(index)) File.Delete(index); }
            var commit = await Commit(project, tree, current, "agent-os integrated: " + work.ShortTask);
            Set(work, WorkStatus.Validating, "Testing the combined candidate against the current shared version. Publication is pending.");
            var evidence = await Validate(work, commit, tree, current, token);
            Mutate(() => work.Evidence.Add(evidence));
            if (!evidence.Passed) { Set(work, WorkStatus.Failed, evidence.SourceUnchanged ? "Validation failed. The candidate remains private; inspect the validation log." : "Validation changed its source inputs. The evidence cannot authorize integration; inspect the validation log."); return; }
            token.ThrowIfCancellationRequested();
            await _profile.Revalidate();await CheckNetworkVersion(work);await PrivateGit.RevalidateModules(work.CandidateModulePins);await PrivateGit.VerifySnapshot(project,(await Commands.Git(project,"rev-parse","HEAD")).Checked(),_profile);
            if(work.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,work.Id)){MarkSdkUnknown(work.Id);return;}
            // Save intent BEFORE the atomic Git compare-and-swap. Recovery inspects this exact commit.
            Mutate(() => { work.PendingCommit = commit; work.PendingBase = current; Event(work.Id, "Publication", "Validated candidate ready; checking the shared version before publication."); });
            if((await Commands.Git(project,"rev-parse",IntegratedRef)).Checked()!=current){Mutate(()=>work.PendingCommit=null);await RecordConflict(work,"UpdateRefRace",work.ChangedPaths,(await Commands.Git(project,"rev-parse",IntegratedRef)).Checked());return;}
            var publish = await Commands.Git(project, "update-ref", IntegratedRef, commit, current);
            if (publish.ExitCode != 0)
            {
                Mutate(() => work.PendingCommit = null);
                var raced = (await Commands.Git(project, "rev-parse", IntegratedRef)).Checked();
                if (raced == current) throw new IOException("Git rejected publication without a changed shared ref: " + publish.Error);
                await RecordConflict(work, "UpdateRefRace", work.ChangedPaths, raced); return;
            }
            Mutate(() =>
            {
                work.IntegratedCommit = commit; work.PendingCommit = null; _state.IntegratedCommit = commit;
                foreach (var notice in _state.Conflicts.Where(x => x.WorkId == work.Id && x.ResolutionRequested && !x.Abandoned && x.Paths.All(work.ChangedPaths.Contains))) { notice.Resolved = true; notice.PublicationBlocked = false; }
                work.Status = _state.Conflicts.Any(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned) ? WorkStatus.Parked : WorkStatus.Completed; work.UpdatedAt = DateTimeOffset.UtcNow;
                work.Detail = work.Status == WorkStatus.Parked ? "Independent edits were validated and integrated; original conflicting work remains parked with its private candidate." : "Validated and integrated into agent-os/integrated. Your checked-out branch has not been switched.";
                UpdateMapStatuses(); Event(work.Id, work.Status == WorkStatus.Parked ? "PartialPublication" : "Completed", work.Detail);
            });
            ScheduleSelectedMapTasks();
        }
        finally { _publication.Release(); }
    }

    private async Task<ValidationEvidence> Validate(WorkUnit work, string commit, string tree, string current, CancellationToken token)
    {
        if(work.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,work.Id))throw new IOException("SDK VM shutdown unproven.");
        var attempt = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_store.Root, "validation", attempt);
        await PrivateGit.VerifyTree(_state.ProjectPath,commit);
        Directory.CreateDirectory(path);
        using var validationClaim=await Coordinator.EnterAsync(new[]{ResourceAdmission.Directory(path,ResourceAccess.Write)},work.ShortTask,null,token);
        (await Commands.Git(_state.ProjectPath,"clone","--no-recurse-submodules","--no-hardlinks","--no-checkout","--",_state.ProjectPath,path)).Checked();
        (await Commands.Git(path,"fetch","--no-recurse-submodules","--no-tags","--no-write-fetch-head",_state.ProjectPath,commit)).Checked();
        await PrivateGit.Checkout(path,commit);
        await PrivateGit.PrepareModules(_state.ProjectPath,path,commit,work.CandidateModulePins);
        await PrivateGit.VerifySnapshot(path,commit,await GitProjectProfile.Inspect(path));
        (await Commands.Git(path,"remote","remove","origin")).Checked();PrivateGit.Prepare(path);
        var log = LogPath(work, "validation-" + attempt + ".log");
        var gitVersion = (await Commands.Git(path, "--version")).Checked();
        var environment = $"{Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {gitVersion}; {work.HostVersion}; model {work.HostModel ?? "fixture"}; PowerShell {FileVersionInfo()}";
        var environmentText = string.Join("\n", Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        var environmentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(environmentText)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        int code;string guestReceipt="";bool shutdownConfirmed=true;bool guestSourceUnchanged=true;SdkGuestResult? sdkGuest=null;
        try
        {
            if(_host is ManagedCodexHost&&WorkExecution.Backend(work)==ExecutionBackend.HyperV)
            {
                var validationWork=new WorkUnit{Id=work.Id,Workspace=path,ExecutionProfileSnapshot=work.ExecutionProfileSnapshot,ExecutionProfileSha256=work.ExecutionProfileSha256};
                var inputHash=WorkExecution.SourceHash(path);using var sdkOwnership=await Coordinator.EnterAsync("private:"+path,work.ShortTask,null,timeout.Token);
                sdkGuest=await WorkExecution.RunSdkAsync(validationWork,_store.Root,work.ValidationCommand,Array.Empty<string>(),300,timeout.Token);
                var verdict=WorkExecution.ValidationResult(sdkGuest,inputHash,WorkExecution.Hash(work.ValidationCommand),WorkExecution.Hash(System.Text.Json.JsonSerializer.Serialize(WorkExecution.Profile(work).HyperV)));
                guestReceipt=verdict.Receipt;shutdownConfirmed=verdict.ShutdownConfirmed;guestSourceUnchanged=verdict.SourceUnchanged;code=verdict.ExitCode;
                File.WriteAllText(log,guestReceipt);environment+="; Hyper-V SDK guest; frozen profile "+work.ExecutionProfileSha256;environmentHash=verdict.EnvironmentSha256;
                if(WorkExecution.HasUnknownOwnership(_store.Root,work.Id))MarkSdkUnknown(work.Id);
            }
            else if (_host is ManagedCodexHost)
            {
                using var sandbox = new ManagedSandbox(path);
                code = (await sandbox.RunAsync(work.ValidationCommand, log, null, timeout.Token)).ExitCode;
                environment += "; Windows AppContainer: no capabilities; private workspace; environment whitelist v1";
                environmentHash = sandbox.EnvironmentSha256 ?? throw new InvalidDataException("Validation environment identity was not captured.");
            }
            else code = await OwnedProcess.RunScript(work.ValidationCommand, path, log, null, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { code=124;if(WorkExecution.Backend(work)==ExecutionBackend.HyperV){shutdownConfirmed=false;MarkSdkUnknown(work.Id);}File.AppendAllText(log,"\nValidation exceeded the 15 minute time limit. Owned processes stopped.\n"); }
        var dirty = (await Commands.Git(path, "status", "--porcelain", "--untracked-files=all")).Checked();
        var afterHead = (await Commands.Git(path, "rev-parse", "HEAD^{tree}")).Checked();
        var evidence = new ValidationEvidence { Commit = commit, Tree = tree, AgainstCommit = current, Command = work.ValidationCommand,
            Environment = environment, EnvironmentSha256 = environmentHash, RuntimeSha256 = StateStore.HashFile(typeof(ProjectRuntime).Assembly.Location), LogPath = log, LogSha256 = StateStore.HashFile(log), GuestReceipt=guestReceipt,GuestInputSha256=sdkGuest?.InputSha256??"",GuestPostSourceSha256=sdkGuest?.PostSourceSha256??"",GuestEffectSha256=sdkGuest?.EffectSha256??"",GuestOwnerReceiptSha256=sdkGuest?.OwnerReceiptSha256??"",ExecutionProfileSha256=sdkGuest==null?"":work.ExecutionProfileSha256,ShutdownConfirmed=shutdownConfirmed,ExitCode = code, SourceUnchanged = guestSourceUnchanged&&dirty.Length == 0 && afterHead == tree };
        if (_host is ManagedCodexHost) await new ValidationLedger(Coordinator.Root, Coordinator).RecordAsync(evidence, token);
        return evidence;
    }
    private async Task<List<ResourceClaim>> ModuleSourceClaims(IEnumerable<ModulePin> pins)
    {
        var claims=new List<ResourceClaim>();foreach(var pin in pins){var profile=await GitProjectProfile.Inspect(pin.SourcePath);if(profile.CommonIdentity!=pin.SourceCommonIdentity||profile.ObjectIdentity!=pin.SourceObjectIdentity)throw new IOException("Pinned module identity changed before admission.");claims.Add(profile.PublicationClaim);}return claims;
    }
    private static IEnumerable<ResourceClaim> Coalesce(IEnumerable<ResourceClaim> claims)
    {
        var unique=claims.DistinctBy(x=>x.FileIdentity??x.Key).ToList();return unique.Where(claim=>!unique.Any(owner=>!ReferenceEquals(owner,claim)&&owner.Kind=="directory"&&owner.Path!=null&&claim.Path!=null&&claim.Path.StartsWith(owner.Path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)));
    }
    private async Task CheckNetworkVersion(WorkUnit work)
    {
        if(!_profile.Network)return;if(work.SourceHead==null||(await Commands.Git(_state.ProjectPath,"rev-parse","HEAD")).Checked()!=work.SourceHead)throw new IOException("Network source version changed after capture.");
    }    private static string FileVersionInfo() => System.Diagnostics.FileVersionInfo.GetVersionInfo(Commands.PowerShell).FileVersion ?? "unknown";

    public async Task<string> ReviseAsync(string id)
    {
        var old = Find(id);
        if (old.IsActive && !_state.HistoricalWorkIds.Contains(old.Id)) throw new InvalidOperationException("Wait for this task to finish or cancel it first.");
        var prompt = old.Task + "\n\nThis is a revision of earlier work. Read the CURRENT files first and preserve changes already integrated by other tasks. " +
            "The previous outcome was: " + old.Detail + "\nPrevious proposed diff (context only; do not apply blindly):\n" + old.Diff;
        return await StartCore(prompt,old.AutoIntegrate,old.Id,null,null,WorkRelationship.Revision);
    }

    public async Task<HumanDecision> RequestReleaseAsync(string id)
    {
        if(WorkExecution.HasUnknownOwnership(_store.Root,id)||Find(id).SdkShutdownDebt)throw new IOException("SDK VM shutdown unproven; release forbidden.");
        var work = Find(id);
        if (work.Status != WorkStatus.Completed || work.IntegratedCommit == null) throw new InvalidOperationException("Prepare and validate an integrated candidate before requesting a release decision.");
        var commit = work.IntegratedCommit;
        var evidence = work.Evidence.LastOrDefault(x => x.Commit == commit && x.Passed) ?? throw new InvalidOperationException("No passing evidence applies to this candidate.");
        if (!File.Exists(evidence.LogPath) || StateStore.HashFile(evidence.LogPath) != evidence.LogSha256) throw new IOException("Validation evidence is missing or changed. A release decision cannot be prepared.");
        var destination = "refs/tags/agent-os/reviewed/" + commit;
        HumanDecision decision;
        lock (_sync)
        {
            var existing = _state.Decisions.FirstOrDefault(x => x.Candidate == commit && x.Destination == destination);
            if (existing != null) return JsonFormat.Copy(existing);
            decision = new HumanDecision { WorkId = id, Candidate = commit, Destination = destination, Status = DecisionStatus.Pending,
                Scope = "Create this local reviewed release tag for this exact commit in this project. No remote push or deployment is authorized.",
                Explanation = $"The candidate passed '{evidence.Command}' at {evidence.TestedAt:u}. Review its diff and evidence. Approve to create an immutable local release marker; reject to retain only the integrated result. Routine integration authority does not include release designation. Other tasks continue while this choice is pending." };
            _state.Decisions.Add(decision); Event(id, "Decision", "A tested release candidate is ready for a scoped human decision."); Save();
        }
        Changed?.Invoke();
        await Task.CompletedTask;
        return JsonFormat.Copy(decision);
    }

    public async Task DecideAsync(string decisionId, bool approve)
    {
        using var updateLease = EnterUpdatePublication();
        await _publication.WaitAsync(_lifetime.Token);
        try
        {
            using var releaseAdmission=await Coordinator.EnterAsync(new[]{_profile.PublicationClaim},"release decision",null,_lifetime.Token);
            HumanDecision decision;
            lock (_sync) decision = _state.Decisions.Single(x => x.Id == decisionId);
            if(WorkExecution.HasUnknownOwnership(_store.Root,decision.WorkId)||Find(decision.WorkId).SdkShutdownDebt)throw new IOException("SDK VM shutdown unproven; release forbidden.");
            if (decision.Status == DecisionStatus.Completed) return;
            if (decision.Status != DecisionStatus.Pending) throw new InvalidOperationException("This decision has already been recorded. Its scope has not been expanded.");
            if (!approve) { Mutate(() => { decision.Status = DecisionStatus.Rejected; decision.DecidedAt = DateTimeOffset.UtcNow; decision.Note = "Release designation declined. Integrated work is retained."; Event(decision.WorkId, "Decision", decision.Note); }); return; }
            var work = Find(decision.WorkId);
            var evidence = work.Evidence.LastOrDefault(x => x.Commit == decision.Candidate && x.Passed);
            var current = (await Commands.Git(_state.ProjectPath, "rev-parse", IntegratedRef)).Checked();
        foreach(var owned in _state.Work.Where(x=>x.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,x.Id))){owned.SdkShutdownDebt=true;owned.Status=WorkStatus.Unknown;owned.Detail="SDK VM shutdown unproven after restart; publication and cleanup disabled.";Event(owned.Id,"Recovery",owned.Detail);}
            var ancestor = await Commands.Git(_state.ProjectPath, "merge-base", "--is-ancestor", decision.Candidate, current);
            if (evidence == null || ancestor.ExitCode != 0 || !File.Exists(evidence.LogPath) || StateStore.HashFile(evidence.LogPath) != evidence.LogSha256)
            { Mutate(() => { decision.Status = DecisionStatus.Stale; decision.Note = "Candidate history or evidence changed. No release tag was created."; }); return; }
            Mutate(() => { decision.Status = DecisionStatus.Approved; decision.DecidedAt = DateTimeOffset.UtcNow; Event(work.Id, "Authority", decision.Scope); });
            var actual = await Commands.Git(_state.ProjectPath, "rev-parse", "--verify", decision.Destination);
            if (actual.ExitCode == 0 && actual.Checked() == decision.Candidate)
            { Mutate(() => { decision.Status = DecisionStatus.Completed; decision.Note = "The exact release marker already exists. No duplicate was created."; }); return; }
            await _profile.Revalidate();
            var result = await Commands.Git(_state.ProjectPath, "update-ref", decision.Destination, decision.Candidate, new string('0', decision.Candidate.Length));
            Mutate(() => { decision.Status = result.ExitCode == 0 ? DecisionStatus.Completed : DecisionStatus.Unknown;
                decision.Note = result.ExitCode == 0 ? "Local reviewed release marker created for the approved commit." : "Could not confirm the intended tag. Inspect Git before retrying; no broader authority was granted.";
                Event(work.Id, "Release", decision.Note); });
        }
        finally { _publication.Release(); }
    }

    public void Cancel(string id)
    {
        var work = Find(id);
        List<string> graph = [];
        lock (_sync)
        {
            var map = _state.Maps.FirstOrDefault(m => m.Tasks.Any(t => t.WorkIds?.Contains(id) == true || t.WorkId == id));
            if (map != null && map.Status != MapStatus.Canceled)
            {
                map.Status = MapStatus.Canceled; map.Revision++;
                graph = map.Tasks.SelectMany(t => t.WorkIds ?? []).Concat(map.Tasks.Select(t => t.WorkId).OfType<string>()).Distinct().ToList();
                UpdateMapStatuses(); Save();
            }
        }
        foreach (var target in graph)
        {
            var owned = Find(target);
            if (owned.Status == WorkStatus.Completed) continue;
            if (_tokens.TryGetValue(target, out var token)) token.Cancel();
            if (!owned.IsActive) Set(owned, WorkStatus.Canceled, "Canceled with the selected map. Private files and evidence are retained.");
        }
        if (work.Status == WorkStatus.Completed) return;
        if (_tokens.TryGetValue(id, out var source)) source.Cancel();
        if (!work.IsActive) Set(work, WorkStatus.Canceled, "Canceled. Private files and evidence are retained.");
    }
    public async Task CleanupAsync(string id)
    {
        var work = Find(id);
        if (work.IsActive || (_jobs.TryGetValue(id, out var job) && !job.IsCompleted)) throw new InvalidOperationException("Cancel and wait for the owned process tree to stop before cleanup.");
        if (_state.Conflicts.Any(x => x.WorkId == id && !x.Resolved && !x.Abandoned)) throw new InvalidOperationException("Resolve or abandon deferred work before cleanup.");
        if(work.SdkShutdownDebt||WorkExecution.HasUnknownOwnership(_store.Root,id))throw new IOException("SDK VM shutdown unproven; cleanup forbidden.");
        await Task.Run(() => SafePaths.DeleteOwnedDirectory(Path.Combine(_store.Root, "workspaces"), work.Workspace));
        Mutate(() => { work.WorkspaceRemoved = true; Event(id, "Cleanup", "Removed this task's private workspace. Candidate, transcript, and validation evidence were retained."); });
    }
    public async Task WaitForIdleAsync()
    {
        // Host continuation and publication can extend a task after a completed turn.
        while (true)
        {
            var pending = _jobs.Values.Where(x => !x.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }
    public string TranscriptPath(string id) => LogPath(Find(id), "codex.jsonl");
    public string DiagnosticsPath(string id) => LogPath(Find(id), "runtime-error.txt");
    private void RecordFailure(WorkUnit work, Exception error)
    {
        try { File.WriteAllText(LogPath(work, "runtime-error.txt"), error.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var unresolved = _state.Conflicts.Where(x => x.WorkId == work.Id && !x.Resolved && !x.Abandoned).ToArray();
        Set(work, unresolved.Length > 0 ? unresolved.Any(x => x.Response == null) ? WorkStatus.NeedsResponse : WorkStatus.Parked : work.PendingCommit == null && work.IntegratedCommit == null ? WorkStatus.Failed : WorkStatus.Unknown,
            error.Message + " Private files and evidence are retained. Inspect runtime diagnostics; an unconfirmed publication is not replayed.");
    }
    private string LogPath(WorkUnit work, string name) { var path = Path.Combine(_store.Root, "evidence", work.Id); Directory.CreateDirectory(path); return Path.Combine(path, name); }
    private WorkUnit Find(string id) { lock (_sync) return _state.Work.Single(x => x.Id == id); }
    public string? PendingSdkOperationId(string id){_=Find(id);return WorkExecution.PendingOperationId(_store.Root,id);}
    public Task<SdkGuestResult> ReconcileSdkAsync(string id,string operationId,CancellationToken token=default)=>ReconcileSdkCore(id,operationId,token,false);
    internal Task<SdkGuestResult> ReconcileSdkFromHostAsync(string id,string operationId,CancellationToken token)=>ReconcileSdkCore(id,operationId,token,true);
    private async Task<SdkGuestResult> ReconcileSdkCore(string id,string operationId,CancellationToken token,bool activeHostTurn)
    {
        var work=Find(id);if(!activeHostTurn&&(work.IsActive||(_jobs.TryGetValue(id,out var job)&&!job.IsCompleted)))throw new InvalidOperationException("Wait for the active work turn before SDK reconciliation.");
        using var ownership=await Coordinator.EnterAsync("private:"+work.Workspace,work.ShortTask,null,token);var receipt=await WorkExecution.ReconcileAsync(work,_store.Root,operationId,token);
        Mutate(()=>Event(id,"SDK reconciliation",WorkExecution.Receipt(receipt)));
        if(!WorkExecution.HasUnknownOwnership(_store.Root,id)&&WorkExecution.ShutdownConfirmed(receipt)&&receipt.TrustedCollector)
        {Mutate(()=>work.SdkShutdownDebt=false);Set(work,receipt.State==WindowsVmState.Failed?WorkStatus.Failed:work.CandidateCommit!=null?WorkStatus.Private:WorkStatus.Unknown,"Exact SDK VM shutdown and protected collector receipt recovered. Review the receipt; resume the private candidate explicitly.");}
        else MarkSdkUnknown(id);return receipt;
    }
    internal void MarkSdkUnknown(string id){var work=Find(id);Mutate(()=>work.SdkShutdownDebt=true);Set(work,WorkStatus.Unknown,"SDK VM shutdown unproven; publication and cleanup disabled.");}    private void Set(WorkUnit work, WorkStatus status, string detail)
    {
        Mutate(() => { work.Status = status; work.Detail = detail; work.UpdatedAt = DateTimeOffset.UtcNow; UpdateMapStatuses(); Event(work.Id, status.ToString(), detail); });
        if (!work.IsActive && status is not (WorkStatus.NeedsResponse or WorkStatus.Parked)) OnInteractionOwnerEnded(work.Id);
        if (status == WorkStatus.Completed) ScheduleSelectedMapTasks();
    }
    private void Event(string? id, string kind, string message)
    { _state.Events.Add(new(DateTimeOffset.UtcNow, id, kind, message)); if (_state.Events.Count > 1000) _state.Events.RemoveRange(0, _state.Events.Count - 1000); }
    private void Save() { LegacyMaps.RestoreStatuses(_state); _store.Save(_state); }
    private void Mutate(Action action) { lock (_sync) { action(); Save(); } Changed?.Invoke(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_broker != null) await _broker.DisposeAsync();
        await WaitForIdleAsync();
        foreach (var source in _tokens.Values) source.Dispose();
        _lifetime.Dispose(); _projectLock.Dispose(); _publication.Dispose(); _runtimeRegistration.Dispose();
    }
}



