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
    private ProjectState _state;
    private bool _disposed;
    private ProjectBroker? _broker;
    public string DataDirectory => _store.Root;
    public event Action? Changed;
    public ProjectState Snapshot { get { lock (_sync) return JsonFormat.Copy(_state); } }
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "projects");
    public static string Coverage => "Codex CLI 0.160.0 is the only supported host. Coordination is automatic. PowerShell edits and validation run in private Windows AppContainers with no network capabilities; native Codex tools are read-only. Private Git status, diff, add, commit and log use a scoped runtime adapter. The runtime rechecks and validates publication into agent-os/integrated, records evidence in its shared SQLite ledger, and owns loopback previews and process trees. Your checked-out branch is separate. External services, deployment, arbitrary native SDKs, network projects, aliases, links and unmanaged programs are not supported mediation surfaces. Unsupported operations do not receive a broader-permission fallback.";

    private ProjectRuntime(StateStore store, FileStream projectLock, ProjectState state, IWorkHost host)
    { _store = store; _projectLock = projectLock; _state = state; _host = host; }

    public static Task<ProjectRuntime> OpenAsync(string project, string? dataRoot = null) => OpenInternal(project, dataRoot, new ManagedCodexHost());
    internal static async Task<ProjectRuntime> OpenInternal(string project, string? dataRoot, IWorkHost host)
    {
        project = SafePaths.Project(project);
        var top = (await Commands.Git(project, "rev-parse", "--show-toplevel")).Checked();
        project = SafePaths.Project(top);
        var gitdir = (await Commands.Git(project, "rev-parse", "--absolute-git-dir")).Checked();
        if (!Directory.Exists(Path.Combine(project, ".git"))) throw new IOException("Open the main Git checkout. Linked worktrees and bare repositories are not supported.");
        FileStream projectLock;
        try { projectLock = new FileStream(Path.Combine(gitdir, "agent-os.runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("This project is already open in another agent-os runtime. Close that window before opening it here."); }
        try
        {
            var head = (await Commands.Git(project, "rev-parse", "--verify", "HEAD")).Checked();
            var entries = (await Commands.Git(project, "ls-tree", "-r", head)).Checked();
            if (entries.Split('\n').Any(x => x.StartsWith("120000 ") || x.StartsWith("160000 ")))
                throw new IOException("This release does not support tracked symbolic links or submodules. Use a project without them.");
            var reference = await Commands.Git(project, "rev-parse", "--verify", IntegratedRef);
            if (reference.ExitCode != 0)
            {
                (await Commands.Git(project, "update-ref", IntegratedRef, head, new string('0', head.Length))).Checked();
                reference = await Commands.Git(project, "rev-parse", IntegratedRef);
            }
            dataRoot = Path.GetFullPath(dataRoot ?? ProjectLocations.Find(project) ?? DefaultDataRoot);
            var store = new StateStore(Path.Combine(dataRoot, StateStore.Key(project)));
            var saved = store.Read();
            var state = saved ?? new ProjectState { ProjectPath = project, IntegratedCommit = reference.Checked(), CodexPath = HostDiscovery.FindCodex() ?? "" };
            if (!string.Equals(state.ProjectPath, project, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Saved project identity does not match this folder.");
            var runtime = new ProjectRuntime(store, projectLock, state, host);
            await runtime.Recover();
            ProjectLocations.Remember(project, dataRoot);
            runtime._broker = new ProjectBroker(runtime);
            return runtime;
        }
        catch { projectLock.Dispose(); throw; }
    }

    private async Task Recover()
    {
        ManagedSandbox.RecoverProfiles();
        var current = (await Commands.Git(_state.ProjectPath, "rev-parse", IntegratedRef)).Checked();
        foreach (var work in _state.Work.Where(x => x.IsActive || x.PendingCommit != null || (x.Status == WorkStatus.Unknown && x.IntegratedCommit != null)))
        {
            var intended = work.PendingCommit ?? work.IntegratedCommit;
            if (intended != null)
            {
                var ancestor = await Commands.Git(_state.ProjectPath, "merge-base", "--is-ancestor", intended, current);
                if (ancestor.ExitCode == 0)
                {
                    work.Status = WorkStatus.Completed; work.IntegratedCommit = intended;
                    work.PendingCommit = null;
                    work.Detail = "Recovered a completed integration from Git. It was not repeated.";
                }
                else { work.Status = WorkStatus.Unknown; work.Detail = "Integration was interrupted. The intended commit is not in the current shared history. Inspect Git before retrying; no effect was replayed."; }
            }
            else { work.Status = WorkStatus.Unknown; work.Detail = "The runtime stopped before confirming completion. Owned process trees were closed; private files are retained. Start a revised task to continue."; }
            Event(work.Id, "Recovery", work.Detail);
        }
        foreach (var decision in _state.Decisions.Where(x => x.Status is DecisionStatus.Approved or DecisionStatus.Unknown))
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

    public Task<string> StartAsync(string task, bool autoIntegrate = true, string? parentId = null, string? externalRequestId = null)
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
                    if (accepted.Task != task.Trim()) throw new ArgumentException("A task request identity cannot be reused for different work.");
                    return Task.FromResult(accepted.Id);
                }
            }
            if (string.IsNullOrWhiteSpace(_state.ValidationCommand)) throw new InvalidOperationException("Save a validation command in Project setup first.");
            WorkUnit? parent = null;
            if (parentId != null) parent = _state.Work.SingleOrDefault(x => x.Id == parentId)
                ?? throw new ArgumentException("The original task does not belong to this project.", nameof(parentId));
            work = new WorkUnit { Task = task.Trim(), ParentId = parentId, AutoIntegrate = autoIntegrate, ValidationCommand = _state.ValidationCommand, ExternalRequestId = externalRequestId };
            work.Title = parent?.ShortTask;
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
            work.BaseCommit = (await Commands.Git(project, "rev-parse", IntegratedRef)).Checked();
            Directory.CreateDirectory(Path.GetDirectoryName(work.Workspace)!);
            (await Commands.Git(project, "clone", "--no-hardlinks", "--no-checkout", "--", project, work.Workspace)).Checked();
            await PrivateGit.Checkout(work.Workspace, work.BaseCommit);
            (await Commands.Git(work.Workspace, "remote", "remove", "origin")).Checked();
            token.ThrowIfCancellationRequested();
            Set(work, WorkStatus.Running, "Codex is working in a private clone. Shared files have not changed.");
            work.HostVersion = await _host.Version(_state.CodexPath);
            var result = await _host.Run(work, _state.CodexPath, LogPath(work, "codex.jsonl"), line =>
            {
                if (line.Length > 3000) line = line[..3000] + "…";
                Mutate(() => { Event(work.Id, "Codex", line); });
            }, token);
            Mutate(() => { work.ThreadId = result.ThreadId; work.CodexReport = result.Report; work.HostModel = result.Model; });
            if (result.ExitCode != 0 || !result.TurnCompleted)
            {
                Set(work, result.ExitCode == 0 ? WorkStatus.Unknown : WorkStatus.Failed,
                    $"Codex exited with code {result.ExitCode}" + (result.TurnCompleted ? "." : " without a confirmed successful turn.") + " Private files and the transcript are retained.");
                return;
            }
            token.ThrowIfCancellationRequested();
            await Capture(work);
            Set(work, WorkStatus.Private, $"Private candidate prepared: {work.ChangedPaths.Count} changed paths. Validation and integration are still required.");
            if (work.AutoIntegrate)
            {
                await IntegrateCore(work, token);
                if (work.Status == WorkStatus.Stale && !token.IsCancellationRequested)
                {
                    var depth = 0; var ancestor = work;
                    lock (_sync) while (ancestor.ParentId != null) { depth++; ancestor = _state.Work.Single(x => x.Id == ancestor.ParentId); }
                    if (depth < 2)
                    {
                        Mutate(() => Event(work.Id, "Revision", "Codex will reread the current shared version and revise this stale candidate within the existing task authority."));
                        await ReviseAsync(work.Id);
                    }
                    else Mutate(() => Event(work.Id, "Revision", "Two automatic revisions were attempted. The stale candidate remains private for investigation; no broader authority was requested."));
                }
            }
        }
        catch (OperationCanceledException) { Set(work, WorkStatus.Canceled, "Canceled. Owned processes stopped; private output is retained. No queued integration will run later."); }
        catch (Exception e) { RecordFailure(work, e); }
    }

    private async Task Capture(WorkUnit work)
    {
        PrivateGit.Prepare(work.Workspace);
        (await Commands.Git(work.Workspace, "add", "-A", "--", ".")).Checked();
        var tree = (await Commands.Git(work.Workspace, "write-tree")).Checked();
        var modes = (await Commands.Git(work.Workspace, "ls-tree", "-r", tree)).Checked();
        if (modes.Split('\n').Any(x => x.StartsWith("120000 ") || x.StartsWith("160000 ")))
            throw new IOException("Candidate contains symbolic links or submodules, which this release cannot mediate.");
        var candidate = await Commit(work.Workspace, tree, work.BaseCommit, "agent-os: " + work.ShortTask);
        (await Commands.Git(_state.ProjectPath, "fetch", "--no-tags", "--no-write-fetch-head", work.Workspace, candidate + ":refs/agent-os/candidates/" + work.Id)).Checked();
        var paths = (await Commands.Git(work.Workspace, "diff", "--name-only", "-z", work.BaseCommit, candidate)).Checked().Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var diff = (await Commands.Git(work.Workspace, "diff", "--no-ext-diff", "--no-textconv", work.BaseCommit, candidate)).Checked();
        Mutate(() => { work.CandidateCommit = candidate; work.ChangedPaths = paths; work.Diff = diff; });
    }

    private static async Task<string> Commit(string cwd, string tree, string parent, string message) =>
        (await Commands.Git(cwd, "-c", "user.name=agent-os", "-c", "user.email=agent-os@localhost", "commit-tree", tree, "-p", parent, "-m", message)).Checked();

    public Task IntegrateAsync(string id)
    {
        var work = Find(id);
        lock (_sync)
        {
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
        Set(work, WorkStatus.Waiting, "Waiting to validate and integrate. Other Codex tasks can continue in their private workspaces.");
        await _publication.WaitAsync(token);
        try
        {
            using var admission = await new MachineCoordinator().EnterAsync("git:" + SafePaths.Project(_state.ProjectPath) + ":" + IntegratedRef, work.ShortTask,
                message => Mutate(() => Event(work.Id, "Coordination", message)), token);
            token.ThrowIfCancellationRequested();
            var project = _state.ProjectPath;
            var current = (await Commands.Git(project, "rev-parse", IntegratedRef)).Checked();
            // Check the complete path identity, including mode and blob, after ownership is acquired.
            foreach (var path in work.ChangedPaths)
            {
                var before = (await Commands.Git(project, "ls-tree", "-z", work.BaseCommit, "--", path)).Checked();
                var now = (await Commands.Git(project, "ls-tree", "-z", current, "--", path)).Checked();
                if (before != now)
                {
                    Set(work, WorkStatus.Stale, $"{path} changed while this candidate was private or waiting. It was not applied. Revise with Codex against the current shared version.");
                    return;
                }
            }
            var index = Path.Combine(_store.Root, "integrate-" + Guid.NewGuid().ToString("N") + ".index");
            string tree;
            try
            {
                var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
                // All touched paths have matched their base identities. Apply their exact delta
                // to a private index of current; no fuzzy/three-way content merge is authorized.
                // This also handles directory/file replacements on older Git versions.
                (await Commands.RunAsync("git", ["read-tree", current], project, token, environment: env)).Checked();
                var patch = await Commands.Git(project, "diff", "--binary", "--full-index", "--no-renames", "--no-ext-diff", "--no-textconv", work.BaseCommit, work.CandidateCommit!, "--");
                patch.Checked();
                var merged = work.ChangedPaths.Count == 0 ? new CommandResult(0, "", "") :
                    await Commands.RunAsync("git", ["apply", "--cached", "--binary", "--whitespace=nowarn"], project, token, input: patch.Output, environment: env);
                var written = await Commands.RunAsync("git", ["write-tree"], project, token, environment: env);
                if (merged.ExitCode != 0 || written.ExitCode != 0) { Set(work, WorkStatus.Stale, "The candidate cannot merge safely into the current shared version. Revise with Codex; no authority grant is needed."); return; }
                tree = written.Checked();
            }
            finally { if (File.Exists(index)) File.Delete(index); }
            var commit = await Commit(project, tree, current, "agent-os integrated: " + work.ShortTask);
            Set(work, WorkStatus.Validating, "Testing the combined candidate against the current shared version. Publication is pending.");
            var evidence = await Validate(work, commit, tree, current, token);
            Mutate(() => work.Evidence.Add(evidence));
            if (!evidence.Passed) { Set(work, WorkStatus.Failed, evidence.SourceUnchanged ? "Validation failed. The candidate remains private; inspect the validation log." : "Validation changed its source inputs. The evidence cannot authorize integration; inspect the validation log."); return; }
            token.ThrowIfCancellationRequested();
            // Save intent BEFORE the atomic Git compare-and-swap. Recovery inspects this exact commit.
            Mutate(() => { work.PendingCommit = commit; work.PendingBase = current; Event(work.Id, "Publication", "Validated candidate ready; checking the shared version before publication."); });
            var publish = await Commands.Git(project, "update-ref", IntegratedRef, commit, current);
            if (publish.ExitCode != 0)
            {
                Mutate(() => work.PendingCommit = null);
                Set(work, WorkStatus.Stale, "The shared branch changed during validation. Publication was refused; revise against its current version."); return;
            }
            Mutate(() =>
            {
                work.IntegratedCommit = commit; work.PendingCommit = null; _state.IntegratedCommit = commit;
                work.Status = WorkStatus.Completed; work.UpdatedAt = DateTimeOffset.UtcNow;
                work.Detail = "Validated and integrated into agent-os/integrated. Your checked-out branch has not been switched.";
                UpdateMapStatuses(); Event(work.Id, "Completed", work.Detail);
            });
            ScheduleSelectedMapTasks();
        }
        finally { _publication.Release(); }
    }

    private async Task<ValidationEvidence> Validate(WorkUnit work, string commit, string tree, string current, CancellationToken token)
    {
        var attempt = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_store.Root, "validation", attempt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        (await Commands.Git(_state.ProjectPath, "clone", "--no-hardlinks", "--no-checkout", "--", _state.ProjectPath, path)).Checked();
        // Unreferenced merge commits are copied by local clone, but explicitly fetch by identity too.
        (await Commands.Git(path, "fetch", "--no-tags", "--no-write-fetch-head", _state.ProjectPath, commit)).Checked();
        await PrivateGit.Checkout(path, commit);
        (await Commands.Git(path, "remote", "remove", "origin")).Checked();
        var log = LogPath(work, "validation-" + attempt + ".log");
        var gitVersion = (await Commands.Git(path, "--version")).Checked();
        var environment = $"{Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {gitVersion}; {work.HostVersion}; model {work.HostModel ?? "fixture"}; PowerShell {FileVersionInfo()}";
        var environmentText = string.Join("\n", Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        var environmentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(environmentText)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        int code;
        try
        {
            if (_host is ManagedCodexHost)
            {
                using var sandbox = new ManagedSandbox(path);
                code = (await sandbox.RunAsync(work.ValidationCommand, log, null, timeout.Token)).ExitCode;
                environment += "; Windows AppContainer: no capabilities; private workspace; environment whitelist v1";
                environmentHash = sandbox.EnvironmentSha256 ?? throw new InvalidDataException("Validation environment identity was not captured.");
            }
            else code = await OwnedProcess.RunScript(work.ValidationCommand, path, log, null, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { code = 124; File.AppendAllText(log, "\nValidation exceeded the 15 minute time limit. Owned processes stopped.\n"); }
        var dirty = (await Commands.Git(path, "status", "--porcelain", "--untracked-files=all")).Checked();
        var afterHead = (await Commands.Git(path, "rev-parse", "HEAD^{tree}")).Checked();
        var evidence = new ValidationEvidence { Commit = commit, Tree = tree, AgainstCommit = current, Command = work.ValidationCommand,
            Environment = environment, EnvironmentSha256 = environmentHash, RuntimeSha256 = StateStore.HashFile(typeof(ProjectRuntime).Assembly.Location), LogPath = log, LogSha256 = StateStore.HashFile(log), ExitCode = code, SourceUnchanged = dirty.Length == 0 && afterHead == tree };
        if (_host is ManagedCodexHost) await new ValidationLedger().RecordAsync(evidence, token);
        return evidence;
    }
    private static string FileVersionInfo() => System.Diagnostics.FileVersionInfo.GetVersionInfo(Commands.PowerShell).FileVersion ?? "unknown";

    public async Task<string> ReviseAsync(string id)
    {
        var old = Find(id);
        if (old.IsActive) throw new InvalidOperationException("Wait for this task to finish or cancel it first.");
        var prompt = old.Task + "\n\nThis is a revision of earlier work. Read the CURRENT files first and preserve changes already integrated by other tasks. " +
            "The previous outcome was: " + old.Detail + "\nPrevious proposed diff (context only; do not apply blindly):\n" + old.Diff;
        return await StartAsync(prompt, old.AutoIntegrate, old.Id);
    }

    public async Task<HumanDecision> RequestReleaseAsync(string id)
    {
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
        await _publication.WaitAsync(_lifetime.Token);
        try
        {
            HumanDecision decision;
            lock (_sync) decision = _state.Decisions.Single(x => x.Id == decisionId);
            if (decision.Status == DecisionStatus.Completed) return;
            if (decision.Status != DecisionStatus.Pending) throw new InvalidOperationException("This decision has already been recorded. Its scope has not been expanded.");
            if (!approve) { Mutate(() => { decision.Status = DecisionStatus.Rejected; decision.DecidedAt = DateTimeOffset.UtcNow; decision.Note = "Release designation declined. Integrated work is retained."; Event(decision.WorkId, "Decision", decision.Note); }); return; }
            var work = Find(decision.WorkId);
            var evidence = work.Evidence.LastOrDefault(x => x.Commit == decision.Candidate && x.Passed);
            var current = (await Commands.Git(_state.ProjectPath, "rev-parse", IntegratedRef)).Checked();
            var ancestor = await Commands.Git(_state.ProjectPath, "merge-base", "--is-ancestor", decision.Candidate, current);
            if (evidence == null || ancestor.ExitCode != 0 || !File.Exists(evidence.LogPath) || StateStore.HashFile(evidence.LogPath) != evidence.LogSha256)
            { Mutate(() => { decision.Status = DecisionStatus.Stale; decision.Note = "Candidate history or evidence changed. No release tag was created."; }); return; }
            Mutate(() => { decision.Status = DecisionStatus.Approved; decision.DecidedAt = DateTimeOffset.UtcNow; Event(work.Id, "Authority", decision.Scope); });
            var actual = await Commands.Git(_state.ProjectPath, "rev-parse", "--verify", decision.Destination);
            if (actual.ExitCode == 0 && actual.Checked() == decision.Candidate)
            { Mutate(() => { decision.Status = DecisionStatus.Completed; decision.Note = "The exact release marker already exists. No duplicate was created."; }); return; }
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
        await Task.Run(() => SafePaths.DeleteOwnedDirectory(Path.Combine(_store.Root, "workspaces"), work.Workspace));
        Mutate(() => { work.WorkspaceRemoved = true; Event(id, "Cleanup", "Removed this task's private workspace. Candidate, transcript, and validation evidence were retained."); });
    }
    public async Task WaitForIdleAsync()
    {
        // A stale candidate may enqueue an authorized Codex revision before its parent finishes.
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
        Set(work, work.PendingCommit == null && work.IntegratedCommit == null ? WorkStatus.Failed : WorkStatus.Unknown,
            error.Message + " Private files and evidence are retained. Inspect runtime diagnostics; an unconfirmed publication is not replayed.");
    }
    private string LogPath(WorkUnit work, string name) { var path = Path.Combine(_store.Root, "evidence", work.Id); Directory.CreateDirectory(path); return Path.Combine(path, name); }
    private WorkUnit Find(string id) { lock (_sync) return _state.Work.Single(x => x.Id == id); }
    private void Set(WorkUnit work, WorkStatus status, string detail)
    {
        Mutate(() => { work.Status = status; work.Detail = detail; work.UpdatedAt = DateTimeOffset.UtcNow; UpdateMapStatuses(); Event(work.Id, status.ToString(), detail); });
        if (status == WorkStatus.Completed) ScheduleSelectedMapTasks();
    }
    private void Event(string? id, string kind, string message)
    { _state.Events.Add(new(DateTimeOffset.UtcNow, id, kind, message)); if (_state.Events.Count > 1000) _state.Events.RemoveRange(0, _state.Events.Count - 1000); }
    private void Save() => _store.Save(_state);
    private void Mutate(Action action) { lock (_sync) { action(); Save(); } Changed?.Invoke(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_broker != null) await _broker.DisposeAsync();
        await WaitForIdleAsync();
        foreach (var source in _tokens.Values) source.Dispose();
        _lifetime.Dispose(); _projectLock.Dispose(); _publication.Dispose();
    }
}



