using AgentOS.Core;
using System.Diagnostics;
using System.Text.Json;

// An executable integration suite keeps runtime verification independent of test-runner packages.
// ScriptHost is internal-only test injection. The shipped application offers only Codex.
if (args.FirstOrDefault() == "coordination-child")
{
    using var ownership = await new MachineCoordinator(args[1]).EnterAsync("shared-fixture", "child", null, CancellationToken.None);
    await File.WriteAllTextAsync(args[2], "ready"); await Task.Delay(TimeSpan.FromMinutes(10)); return 0;
}
if (args.FirstOrDefault() == "coordination-benchmark")
{
    var coordinator = new MachineCoordinator(args[1]);
    var measurements = new List<double>();
    for (var i = 0; i < 11; i++)
    {
        var timer = Stopwatch.StartNew(); using (await coordinator.EnterAsync("derived-fixture-resource", "fixture", null, CancellationToken.None))
        { if (args.Length > 2) (await Commands.RunAsync("node", [args[2]], args[1])).Checked(); }
        measurements.Add(timer.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine(JsonSerializer.Serialize(measurements)); return 0;
}
if (args.FirstOrDefault() == "crash-child")
{
    await using var child = await ProjectRuntime.OpenInternal(args[1], args[2], new ScriptHost());
    child.Configure("Write-Output passed");
    await child.StartAsync("Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 600' -PassThru | ForEach-Object { Set-Content child.pid $_.Id }; Set-Content ready.txt ready; Start-Sleep -Seconds 600");
    await child.WaitForIdleAsync(); return 0;
}

var root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "artifacts/tests/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
var results = new List<object>();
var filter = args.ElementAtOrDefault(1);
int failures = 0;
async Task Test(string name, Func<Task> test)
{
    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    var timer = Stopwatch.StartNew();
    try { await test(); results.Add(new { name, passed = true, seconds = timer.Elapsed.TotalSeconds }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, passed = false, seconds = timer.Elapsed.TotalSeconds, error = e.ToString() }); Console.WriteLine("FAIL " + name + "\n" + e); }
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { source = await SourceIdentity(), coreSha256 = StateStore.HashFile(typeof(ProjectRuntime).Assembly.Location), testSha256 = StateStore.HashFile(typeof(Program).Assembly.Location), environment = Environment.OSVersion.ToString(), dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, results }, JsonFormat.Options));
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Eventually(Func<bool> condition, int seconds = 30)
{ using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); while (!condition()) await Task.Delay(50, timeout.Token); }
async Task<ProjectRuntime> NewRuntime()
{ var project = await PracticeProject.CreateAsync(root); return await ProjectRuntime.OpenInternal(project, Path.Combine(root, "state"), new ScriptHost()); }
static WorkUnit Work(ProjectRuntime runtime, string id) => runtime.Snapshot.Work.Single(w => w.Id == id);
const string ChangeRetries = "Set-Content settings.json '{\"retries\":2,\"cancellation\":false}'; git diff";
const string ChangeCancel = "Set-Content settings.json '{\"retries\":1,\"cancellation\":true}'; git diff";

await Test("Task maps remain drafts until selected dependencies are ready", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var first = new MapTask { Title = "First", Prompt = "Add-Content README.md 'First map step'", Acceptance = "README contains first step", Selected = true };
    var second = new MapTask { Title = "Second", Prompt = "Add-Content README.md 'Second map step'", Acceptance = "README contains both steps", Selected = true };
    var map = new AgentOS.Core.TaskMap { Title = "Ordered work", Tasks = [first, second], Edges = [new MapEdge(first.Id, second.Id, MapEdgeKind.Dependency)] };
    var id = r.SaveDraftMap(map);
    Assert(r.Snapshot.Work.Count == 0 && r.Snapshot.Maps.Single().Status == MapStatus.Draft, "Saving a draft started execution.");
    var launched = await r.StartSelectedMapTasksAsync(id); await r.WaitForIdleAsync();
    Assert(launched.Count == 1 && r.Snapshot.Maps.Single().Tasks[1].WorkId == null, "Dependency was bypassed.");
    var next = await r.StartSelectedMapTasksAsync(id); await r.WaitForIdleAsync();
    Assert(next.Count == 1 && next[0] != launched[0], "Completed dependency did not unlock the next task.");
    var revised = r.Snapshot.Maps.Single(); revised.Edges.Add(new MapEdge(second.Id, first.Id, MapEdgeKind.Dependency));
    var refused = false; try { TaskMapRules.Validate(revised); } catch (ArgumentException) { refused = true; }
    Assert(refused, "A dependency cycle was accepted.");
});
await Test("Old state keeps history and captured citations cannot be rewritten", async () =>
{
    var legacy = JsonSerializer.Serialize(new { Schema = 1, ProjectPath = @"C:\old", Work = new[] { new WorkUnit { Task = "legacy task" } } }, JsonFormat.Options);
    var store = new StateStore(Path.Combine(root, "map-migration-" + Guid.NewGuid().ToString("N")));
    File.WriteAllText(store.StatePath, legacy);
    var migrated = store.Read()!;
    Assert(migrated.Schema == 2 && migrated.Maps.Count == 0 && migrated.Work.Single().Task == "legacy task", "Schema-1 history was lost.");
    Assert(File.ReadAllText(store.StatePath + ".schema1.bak") == legacy, "Migration backup differs from old history.");
    store.Save(migrated); Assert(store.Read()!.Work.Single().Task == "legacy task", "Saved migration lost history.");
    await using var r = await NewRuntime();
    var citation = ContextCitation.Create("foreground-title", System.Text.Encoding.UTF8.GetBytes("example"));
    var map = new AgentOS.Core.TaskMap { Title = "Captured context", Tasks = [new MapTask { Title = "Inspect", Prompt = "Write-Output inspect", Acceptance = "Inspection recorded" }], Citations = [citation] };
    var id = r.SaveDraftMap(map); var edited = r.Snapshot.Maps.Single(m => m.Id == id);
    edited.Citations[0] = edited.Citations[0] with { Sha256 = new string('0', 64) };
    var rejected = false; try { r.SaveDraftMap(edited, edited.Revision); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Captured citation rewrite was accepted.");
});
await Test("CLI broker uses the open project authority for maps", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var path = r.Snapshot.ProjectPath;
    var task = new MapTask { Title = "Inspect", Prompt = "Write-Output inspect", Acceptance = "Inspection recorded", Selected = true };
    var draft = new AgentOS.Core.TaskMap { Title = "Broker draft", Tasks = [task] };
    var saved = await ProjectClient.SaveMapAsync(path, draft, null);
    Assert(saved?.Id == draft.Id && r.Snapshot.Work.Count == 0, "Broker save did not preserve draft-only behavior.");
    var listed = await ProjectClient.MapsAsync(path);
    Assert(listed?.Single().Id == draft.Id, "Broker list missed the saved map.");
    var started = await ProjectClient.StartMapAsync(path, draft.Id);
    Assert(started?.Count == 1, "Broker failed to start selected task.");
    await r.WaitForIdleAsync();
});
await Test("Private source, Git indexes, and ordinary shell are separate", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var baseline = r.Snapshot.IntegratedCommit;
    var id = await r.StartAsync(ChangeRetries, false); await r.WaitForIdleAsync();
    Assert(Work(r, id).Status == WorkStatus.Private, Work(r, id).Detail);
    Assert(r.Snapshot.IntegratedCommit == baseline, "Private work changed shared state.");
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "status", "--porcelain")).Checked() == "", "Original checkout became dirty.");
    Assert(Work(r, id).ChangedPaths.SequenceEqual(["settings.json"]), "Candidate paths incorrect.");
});

await Test("Real contention: stale writer refused, independent candidate revalidated", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Start-Sleep -Seconds 2; Write-Output passed");
    var a = await r.StartAsync(ChangeRetries, false);
    var b = await r.StartAsync(ChangeCancel, false);
    var c = await r.StartAsync("Add-Content README.md 'Independent work'", false);
    await r.WaitForIdleAsync();
    Assert(r.Snapshot.Work.All(w => w.Status == WorkStatus.Private), string.Join("\n", r.Snapshot.Work.Select(w => w.Detail)));
    var first = r.IntegrateAsync(a); await Eventually(() => Work(r, a).Status == WorkStatus.Validating);
    var second = r.IntegrateAsync(b); var third = r.IntegrateAsync(c);
    await Eventually(() => Work(r, b).Status == WorkStatus.Waiting && Work(r, c).Status == WorkStatus.Waiting);
    Assert(!first.IsCompleted, "Wait was not exercised under actual contention.");
    await Task.WhenAll(first, second, third);
    Assert(Work(r, a).Status == WorkStatus.Completed, Work(r, a).Detail);
    Assert(Work(r, b).Status == WorkStatus.Stale, Work(r, b).Detail);
    Assert(Work(r, c).Status == WorkStatus.Completed, Work(r, c).Detail);
    Assert(Work(r, c).Evidence.Single().AgainstCommit == Work(r, a).IntegratedCommit, "Independent candidate wasn't retested on combined input.");
    var shared = (await Commands.Git(r.Snapshot.ProjectPath, "show", ProjectRuntime.IntegratedRef + ":settings.json")).Checked();
    Assert(shared.Contains("\"retries\":2") && shared.Contains("\"cancellation\":false"), "Stale candidate escaped.");
    Assert(r.Snapshot.Decisions.Count == 0, "Contention incorrectly asked for authority.");
});

await Test("Failed validation and mutated source cannot publish", async () =>
{
    await using var r = await NewRuntime(); var baseline = r.Snapshot.IntegratedCommit;
    r.Configure("Write-Error 'deliberate failed validation'; exit 7");
    var a = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync();
    Assert(Work(r, a).Status == WorkStatus.Failed && Work(r, a).Evidence.Count == 1, Work(r, a).Detail);
    r.Configure("Set-Content README.md 'Validation unexpectedly changes source'; exit 0");
    var b = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync();
    Assert(Work(r, b).Status == WorkStatus.Failed && !Work(r, b).Evidence.Single().SourceUnchanged, "Changed source passed.");
    Assert(r.Snapshot.IntegratedCommit == baseline, "Failed validation published.");
});

await Test("Scoped authority persists, deduplicates, and permits independent work", async () =>
{
    var r = await NewRuntime(); r.Configure("Write-Output passed");
    var a = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync();
    var d = await r.RequestReleaseAsync(a);
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "rev-parse", "--verify", d.Destination)).ExitCode != 0, "Release escaped before approval.");
    var b = await r.StartAsync("Add-Content README.md 'Work while decision is pending'"); await r.WaitForIdleAsync();
    Assert(Work(r, b).Status == WorkStatus.Completed && r.Snapshot.Decisions[0].Status == DecisionStatus.Pending, "Decision blocked independent work.");
    await r.DecideAsync(d.Id, true); await r.DecideAsync(d.Id, true);
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "rev-parse", d.Destination)).Checked() == d.Candidate, "Approval changed candidate.");
    Assert((await r.RequestReleaseAsync(a)).Id == d.Id, "Equivalent operation prompted again.");
    var d2 = await r.RequestReleaseAsync(b); Assert(d2.Id != d.Id && d2.Status == DecisionStatus.Pending, "Authority widened to a new candidate.");
    await r.DecideAsync(d2.Id, false);
    var project = r.Snapshot.ProjectPath; var data = Directory.GetParent(r.DataDirectory)!.FullName;
    await r.DisposeAsync(); await using var reopened = await ProjectRuntime.OpenInternal(project, data, new ScriptHost());
    Assert((await reopened.RequestReleaseAsync(a)).Status == DecisionStatus.Completed, "Grant lost on restart.");
    Assert(reopened.Snapshot.Decisions.Single(x => x.Id == d2.Id).Status == DecisionStatus.Rejected, "Rejection lost.");
});

await Test("Queued cancellation prevents late publication", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Start-Sleep -Seconds 3; Write-Output passed");
    var a = await r.StartAsync(ChangeRetries, false); var b = await r.StartAsync("Add-Content README.md 'Canceled candidate'", false); await r.WaitForIdleAsync();
    var first = r.IntegrateAsync(a); await Eventually(() => Work(r, a).Status == WorkStatus.Validating);
    var second = r.IntegrateAsync(b); await Eventually(() => Work(r, b).Status == WorkStatus.Waiting); r.Cancel(b);
    await Task.WhenAll(first, second);
    Assert(Work(r, b).Status == WorkStatus.Canceled, "Canceled queue entry ran.");
    Assert(!(await Commands.Git(r.Snapshot.ProjectPath, "show", ProjectRuntime.IntegratedRef + ":README.md")).Checked().Contains("Canceled candidate"), "Canceled output escaped.");
});

await Test("Cancellation kills owned child processes and retains forensic files", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var id = await r.StartAsync("Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 600' -PassThru | ForEach-Object { Set-Content child.pid $_.Id }; Start-Sleep -Seconds 600");
    await Eventually(() => File.Exists(Path.Combine(Work(r, id).Workspace, "child.pid")));
    var pid = int.Parse(File.ReadAllText(Path.Combine(Work(r, id).Workspace, "child.pid")));
    r.Cancel(id); await r.WaitForIdleAsync();
    Assert(Work(r, id).Status == WorkStatus.Canceled, Work(r, id).Detail);
    await Eventually(() => !ProcessExists(pid));
    Assert(Directory.Exists(Work(r, id).Workspace), "Cancellation discarded private output.");
    await r.CleanupAsync(id); Assert(!Directory.Exists(Work(r, id).Workspace), "Cleanup failed.");
    Assert(File.Exists(r.TranscriptPath(id)), "Cleanup lost evidence.");
});

await Test("Host success without a completion event is unknown", async () =>
{
    var project = await PracticeProject.CreateAsync(root);
    await using var r = await ProjectRuntime.OpenInternal(project, Path.Combine(root, "state"), new ScriptHost(false)); r.Configure("Write-Output passed");
    var id = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync();
    Assert(Work(r, id).Status == WorkStatus.Unknown && Work(r, id).CandidateCommit == null, "Unconfirmed success was published.");
});

await Test("Another runtime cannot own the same project", async () =>
{
    await using var r = await NewRuntime(); bool refused = false;
    try { await using var other = await ProjectRuntime.OpenAsync(r.Snapshot.ProjectPath, Path.Combine(root, "different-state")); }
    catch (IOException e) { refused = e.Message.Contains("already open"); }
    Assert(refused, "A second runtime obtained ownership.");
});

await Test("Runtime crash kills surviving descendants and recovers as unknown", async () =>
{
    var project = await PracticeProject.CreateAsync(root); var data = Path.Combine(root, "crash-state");
    var exe = Environment.ProcessPath!;
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(exe) == "dotnet") start.ArgumentList.Add(typeof(ScriptHost).Assembly.Location);
    foreach (var arg in new[] { "crash-child", project, data }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    string? pidFile = null;
    await Eventually(() => { pidFile = Directory.Exists(data) ? Directory.GetFiles(data, "child.pid", SearchOption.AllDirectories).FirstOrDefault() : null; return pidFile != null && File.ReadAllText(pidFile).Trim().Length > 0; });
    var childPid = int.Parse(File.ReadAllText(pidFile!));
    process.Kill(); await process.WaitForExitAsync(); await Eventually(() => !ProcessExists(childPid));
    await using var r = await ProjectRuntime.OpenInternal(project, data, new ScriptHost());
    Assert(r.Snapshot.Work.Single().Status == WorkStatus.Unknown, "Restart invented a successful result.");
});

await Test("Lost integration acknowledgement recovers exact result without replay", async () =>
{
    var r = await NewRuntime(); r.Configure("Write-Output passed");
    var id = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync();
    var state = r.Snapshot; var work = state.Work.Single();
    Assert(work.Status == WorkStatus.Completed, work.Detail);
    work.PendingCommit = work.IntegratedCommit; work.IntegratedCommit = null; work.Status = WorkStatus.Validating;
    var dir = r.DataDirectory; await r.DisposeAsync(); new StateStore(dir).Save(state);
    await using var recovered = await ProjectRuntime.OpenInternal(state.ProjectPath, Directory.GetParent(dir)!.FullName, new ScriptHost());
    Assert(Work(recovered, id).Status == WorkStatus.Completed, "Known successful publication was not recovered.");
    Assert((await Commands.Git(state.ProjectPath, "rev-parse", ProjectRuntime.IntegratedRef)).Checked() == state.IntegratedCommit, "Recovery duplicated commit.");
});

await Test("Changed evidence prevents release approval", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var id = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync(); var d = await r.RequestReleaseAsync(id);
    await File.AppendAllTextAsync(Work(r, id).Evidence.Single().LogPath, "tampered"); await r.DecideAsync(d.Id, true);
    Assert(r.Snapshot.Decisions.Single().Status == DecisionStatus.Stale, "Changed evidence retained authority.");
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "rev-parse", "--verify", d.Destination)).ExitCode != 0, "Tag created with changed evidence.");
});

await Test("Direct same-user bypass is detected as an unsupported boundary", async () =>
{
    await using var r = await NewRuntime(); var external = Path.Combine(r.Snapshot.ProjectPath, "bypass-proof.txt");
    r.Configure("Write-Output passed");
    var id = await r.StartAsync("Set-Content -LiteralPath " + Commands.Quote(external) + " 'same-user shell can write outside clone'", false);
    await r.WaitForIdleAsync();
    Assert(File.Exists(external), "Expected limitation changed: re-evaluate boundary and upgrade guarantee tests.");
    Assert(ProjectRuntime.Coverage.Contains("unmanaged programs are not supported mediation surfaces"), "UI hides boundary limitation.");
});

await Test("Automatic integration revises stale work within bounded task authority", async () =>
{
    var project = await PracticeProject.CreateAsync(root);
    await using var r = await ProjectRuntime.OpenInternal(project, Path.Combine(root, "state"), new RevisingHost());
    r.Configure("Write-Output passed");
    var a = await r.StartAsync("retry"); var b = await r.StartAsync("cancel");
    await r.WaitForIdleAsync();
    Assert(Work(r, a).Status == WorkStatus.Completed, Work(r, a).Detail);
    Assert(Work(r, b).Status == WorkStatus.Stale, Work(r, b).Detail);
    var revision = r.Snapshot.Work.Single(w => w.ParentId == b);
    Assert(revision.Status == WorkStatus.Completed, revision.Detail);
    Assert(r.Snapshot.Decisions.Count == 0, "Routine revision required human authority.");
    var final = (await Commands.Git(project, "show", ProjectRuntime.IntegratedRef + ":settings.json")).Checked();
    using var json = JsonDocument.Parse(final);
    Assert(json.RootElement.GetProperty("retries").GetInt32() == 2 && json.RootElement.GetProperty("cancellation").GetBoolean(), "Revision lost prior work.");
});

await Test("External ref change during validation refuses compare-and-swap", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Start-Sleep -Seconds 3; Write-Output passed");
    var id = await r.StartAsync(ChangeRetries, false); await r.WaitForIdleAsync();
    var integration = r.IntegrateAsync(id); await Eventually(() => Work(r, id).Status == WorkStatus.Validating);
    // A same-user external actor changes the managed ref. This must defeat the expected-old check.
    (await Commands.Git(r.Snapshot.ProjectPath, "update-ref", ProjectRuntime.IntegratedRef, Work(r, id).CandidateCommit!)).Checked();
    await integration;
    Assert(Work(r, id).Status == WorkStatus.Stale && Work(r, id).Evidence.Single().Passed, "A changed shared ref published tested work under stale assumptions.");
});

await Test("Unicode survives the Windows process adapter", async () =>
{
    var log = Path.Combine(root, "unicode.log");
    var code = await OwnedProcess.RunScript("Write-Output 'café — 日本語'", root, log, null, CancellationToken.None);
    Assert(code == 0 && File.ReadAllText(log).Contains("café — 日本語"), "Native host output lost Unicode.");
});

await Test("Corrupt durable state is preserved and refused", async () =>
{
    var r = await NewRuntime(); var project = r.Snapshot.ProjectPath; var dir = r.DataDirectory; await r.DisposeAsync();
    var file = Path.Combine(dir, "state.json"); await File.WriteAllTextAsync(file, "{ broken state");
    var refused = false;
    try { await using var other = await ProjectRuntime.OpenInternal(project, Directory.GetParent(dir)!.FullName, new ScriptHost()); }
    catch (JsonException) { refused = true; }
    Assert(refused && File.ReadAllText(file) == "{ broken state", "Corrupt evidence was reset.");
});

await Test("A concurrent state reader cannot break durable progress", async () =>
{
    await using var r = await NewRuntime();
    // PowerShell Get-Content and third-party inspectors can omit FileShare.Delete.
    var reader = new FileStream(Path.Combine(r.DataDirectory, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var writer = Task.Run(() => r.Configure("Write-Output reader-race-passed"));
    await Task.Delay(80); reader.Dispose(); await writer;
    var saved = new StateStore(r.DataDirectory).Read()!;
    Assert(saved.ValidationCommand == "Write-Output reader-race-passed", "A reader prevented durable progress.");
});

await Test("Failed output persistence stops the owned process promptly", async () =>
{
    var timer = Stopwatch.StartNew(); var failed = false;
    try
    {
        await OwnedProcess.RunScript("Write-Output 'first event'; Start-Sleep -Seconds 60", root,
            Path.Combine(root, "callback-failure.log"), _ => throw new IOException("injected persistence failure"), CancellationToken.None);
    }
    catch (IOException e) { failed = e.Message.Contains("injected persistence failure"); }
    Assert(failed && timer.Elapsed < TimeSpan.FromSeconds(8), "Persistence failure stranded its producer or was hidden.");
});

await Test("Reference OV3 OV4: directory overlap blocks but prefix sibling and case variants are understood", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    await r.StartAsync("New-Item -ItemType Directory area,area-old | Out-Null; Set-Content area/item.txt original; Set-Content area-old/item.txt original");
    await r.WaitForIdleAsync();
    var a = await r.StartAsync("Remove-Item area/item.txt; Remove-Item area; Set-Content area replacement", false);
    var b = await r.StartAsync("Set-Content area/item.txt changed", false);
    var c = await r.StartAsync("Set-Content area-old/item.txt independent", false);
    await r.WaitForIdleAsync();
    await r.IntegrateAsync(a); await r.IntegrateAsync(b); await r.IntegrateAsync(c);
    Assert(Work(r, a).Status == WorkStatus.Completed && Work(r, b).Status == WorkStatus.Stale && Work(r, c).Status == WorkStatus.Completed, "Directory replacement lost overlap or blocked a prefix sibling.");
    var first = await r.StartAsync(ChangeRetries, false);
    var caseVariant = await r.StartAsync("Set-Content SETTINGS.json '{\"retries\":1,\"cancellation\":true}'", false);
    await r.WaitForIdleAsync(); await r.IntegrateAsync(first); await r.IntegrateAsync(caseVariant);
    Assert(Work(r, caseVariant).Status == WorkStatus.Stale, "Windows case variant escaped stale detection.");
});

await Test("Reference ST1 AK3 PR12: stop and cleanup preserve a pending decision and peer workspace", async () =>
{
    var r = await NewRuntime(); r.Configure("Write-Output passed");
    var id = await r.StartAsync(ChangeRetries); await r.WaitForIdleAsync();
    var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => r.RequestReleaseAsync(id))));
    Assert(requests.Select(x => x.Id).Distinct().Count() == 1 && r.Snapshot.Decisions.Count == 1, "Concurrent equivalent requests duplicated decisions.");
    var peer = await r.StartAsync("Set-Content ready.txt ready; Start-Sleep -Seconds 60");
    await Eventually(() => File.Exists(Path.Combine(Work(r, peer).Workspace, "ready.txt")));
    await r.CleanupAsync(id);
    Assert(File.Exists(Path.Combine(Work(r, peer).Workspace, "ready.txt")), "Cleanup touched the peer workspace.");
    r.Cancel(peer); await r.WaitForIdleAsync();
    Assert(Work(r, peer).Status == WorkStatus.Canceled && r.Snapshot.Decisions.Single().Status == DecisionStatus.Pending, "Stopping a peer altered release authority.");
    var project = r.Snapshot.ProjectPath; var dataRoot = Path.GetDirectoryName(r.DataDirectory)!;
    await r.DisposeAsync();
    await using var recovered = await ProjectRuntime.OpenInternal(project, dataRoot, new ScriptHost());
    Assert(recovered.Snapshot.Decisions.Single().Status == DecisionStatus.Pending && Work(recovered, id).WorkspaceRemoved, "Pending decision or cleanup was lost on restart.");
    await recovered.DecideAsync(requests[0].Id, true);
    Assert(recovered.Snapshot.Decisions.Single().Status == DecisionStatus.Completed, "Retained evidence could not support the scoped decision after cleanup.");
});

await Test("Reference ST3 ST7: repeated stale revisions stop at the bound and retain parent outcomes", async () =>
{
    var project = await PracticeProject.CreateAsync(root);
    var host = new RepeatedContentionHost(project);
    await using var r = await ProjectRuntime.OpenInternal(project, Path.Combine(root, "state"), host);
    r.Configure("Write-Output passed");
    await r.StartAsync("Update settings without overwriting newer work"); await r.WaitForIdleAsync();
    var work = r.Snapshot.Work;
    Assert(host.Runs == 3 && work.Count == 3 && work.All(x => x.Status == WorkStatus.Stale), "Automatic revision was unbounded or overwrote a parent outcome.");
    Assert(work[1].ParentId == work[0].Id && work[2].ParentId == work[1].Id && r.Snapshot.Decisions.Count == 0, "Revision lineage was lost or contention asked for authority.");
    Assert(r.Snapshot.Events.Any(x => x.Message.StartsWith("Two automatic revisions")), "The retry bound was not explained.");
});

await Test("Reference LF6: invalid task identity cannot create a child or target cleanup", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var refused = false;
    try { await r.StartAsync("Write-Output forbidden", parentId: "../../outside"); } catch (ArgumentException) { refused = true; }
    Assert(refused && r.Snapshot.Work.Count == 0, "Invalid parent left a partial work record.");
    refused = false;
    try { await r.CleanupAsync("../../outside"); } catch (InvalidOperationException) { refused = true; }
    Assert(refused && Directory.Exists(r.Snapshot.ProjectPath), "Unknown identity reached path cleanup.");
});

await Test("Exact candidate delta preserves binary files and renames across independent publication", async () =>
{
    await using var r = await NewRuntime(); r.Configure("Write-Output passed");
    var binary = await r.StartAsync("[IO.File]::WriteAllBytes((Join-Path (Get-Location) 'bytes.bin'), [byte[]](0,255,10,128,0,42)); Move-Item README.md 'café.md'", false);
    var other = await r.StartAsync(ChangeRetries, false); await r.WaitForIdleAsync();
    await r.IntegrateAsync(other); await r.IntegrateAsync(binary);
    Assert(Work(r, binary).Status == WorkStatus.Completed, Work(r, binary).Detail);
    var candidateBlob = (await Commands.Git(r.Snapshot.ProjectPath, "rev-parse", Work(r, binary).CandidateCommit + ":bytes.bin")).Checked();
    var sharedBlob = (await Commands.Git(r.Snapshot.ProjectPath, "rev-parse", ProjectRuntime.IntegratedRef + ":bytes.bin")).Checked();
    Assert(candidateBlob == sharedBlob, "Binary content changed during publication.");
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "show", ProjectRuntime.IntegratedRef + ":café.md")).ExitCode == 0, "Unicode rename was lost.");
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "show", ProjectRuntime.IntegratedRef + ":README.md")).ExitCode != 0, "Rename retained the deleted path.");
    Assert((await Commands.Git(r.Snapshot.ProjectPath, "show", ProjectRuntime.IntegratedRef + ":settings.json")).Checked().Contains("\"retries\":2"), "Independent content was lost.");
});

await Test("Existing project broker admits CLI work, preserves policy and deduplicates retries", async () =>
{
    await using var runtime = await NewRuntime(); runtime.Configure("Write-Output passed");
    var request = Guid.NewGuid().ToString("N");
    var first = await ProjectClient.TryStartAsync(runtime.Snapshot.ProjectPath, "Set-Content broker.txt 'real effect'", request, "Write-Output passed");
    var retry = await ProjectClient.TryStartAsync(runtime.Snapshot.ProjectPath, "Set-Content broker.txt 'real effect'", request, "Write-Output passed");
    Assert(first != null && retry?.Id == first.Id && runtime.Snapshot.Work.Count == 1, "A retried launch created another task.");
    await runtime.WaitForIdleAsync();
    var work = await ProjectClient.InspectAsync(runtime.Snapshot.ProjectPath, first!.Id);
    Assert(work?.Status == WorkStatus.Completed && (await Commands.Git(runtime.Snapshot.ProjectPath, "show", ProjectRuntime.IntegratedRef + ":broker.txt")).Checked() == "real effect", "Broker did not execute real work.");
    try { await ProjectClient.TryStartAsync(runtime.Snapshot.ProjectPath, "other", Guid.NewGuid().ToString("N"), "different policy"); throw new Exception("Broker changed validation policy."); }
    catch (InvalidOperationException) { }
    Assert(runtime.Snapshot.ValidationCommand == "Write-Output passed" && runtime.Snapshot.Work.Count == 1, "Authority was broadened to admit a task.");
    var project = runtime.Snapshot.ProjectPath; await runtime.DisposeAsync();
    await using var reopened = await ProjectRuntime.OpenInternal(project, null, new ScriptHost());
    var recovered = await ProjectClient.TryStartAsync(project, "Set-Content broker.txt 'real effect'", request, "Write-Output passed");
    Assert(recovered?.Id == first.Id && reopened.Snapshot.Work.Count == 1 && recovered.Status == WorkStatus.Completed, "A persisted launch was replayed after restart.");
});

await Test("Project discovery retains a custom evidence root across runtime restart", async () =>
{
    var project = await PracticeProject.CreateAsync(root); string original;
    await using (var runtime = await ProjectRuntime.OpenInternal(project, Path.Combine(root, "custom-broker-root"), new ScriptHost()))
    { runtime.Configure("Write-Output passed"); original = runtime.DataDirectory; }
    await using var reopened = await ProjectRuntime.OpenInternal(project, null, new ScriptHost());
    Assert(reopened.DataDirectory == original && reopened.Snapshot.ValidationCommand == "Write-Output passed", "Project discovery opened a different history.");
});

await Test("Machine coordination waits fairly, admits independent work and cancels waits", async () =>
{
    var coordinator = new MachineCoordinator(Path.Combine(root, "machine"));
    using var first = await coordinator.EnterAsync("shared", "first", null, CancellationToken.None);
    using var cancel = new CancellationTokenSource();
    var second = coordinator.EnterAsync("SHARED", "second", null, cancel.Token);
    await Eventually(() => coordinator.Snapshot().Count == 2);
    using var independent = await coordinator.EnterAsync("independent", "third", null, CancellationToken.None);
    Assert(!second.IsCompleted, "Conflict ran concurrently.");
    cancel.Cancel(); try { using var unexpected = await second; throw new Exception("Canceled wait admitted."); } catch (OperationCanceledException) { }
    Assert(coordinator.Snapshot().Count == 2, "Canceled wait remained queued.");
    first.Dispose(); using var resumed = await coordinator.EnterAsync("shared", "fourth", null, CancellationToken.None);
});

await Test("Trusted runtime commands stop descendants after their foreground process exits", async () =>
{
    var result = await Commands.RunAsync(Commands.PowerShell, ["-NoProfile", "-Command", "$child=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 600' -PassThru; Write-Output $child.Id"], root);
    var pid = int.Parse(result.Checked());
    await Eventually(() => !ProcessExists(pid));
});

await Test("Machine coordination survives process exit without expiring a live writer", async () =>
{
    var coordination = Path.Combine(root, "cross-process"); var ready = Path.Combine(root, "owner-ready.txt");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { "coordination-child", coordination, ready }) start.ArgumentList.Add(arg);
    using var child = Process.Start(start)!;
    try
    {
        await Eventually(() => File.Exists(ready));
        var coordinator = new MachineCoordinator(coordination);
        var next = coordinator.EnterAsync("shared-fixture", "next", null, CancellationToken.None);
        await Task.Delay(200); Assert(!next.IsCompleted, "A live owner was ignored.");
        child.Kill(true); await child.WaitForExitAsync(); using var recovered = await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(File.ReadAllText(Path.Combine(coordination, "journal.json")).Contains("Unknown after owner exit"), "Crash was reported as success.");
    }
    finally { if (!child.HasExited) child.Kill(true); }
});

await Test("Private Git refuses a hard-linked configuration before trusted writes", async () =>
{
    var project = await PracticeProject.CreateAsync(root); var outside = Path.Combine(root, "protected-config.txt"); await File.WriteAllTextAsync(outside, "original");
    File.Delete(Path.Combine(project, ".git", "config"));
    (await Commands.RunAsync(Commands.PowerShell, ["-NoProfile", "-Command", "New-Item -ItemType HardLink -Path " + Commands.Quote(Path.Combine(project, ".git", "config")) + " -Target " + Commands.Quote(outside) + " | Out-Null"], project)).Checked();
    try { PrivateGit.Prepare(project); throw new Exception("Hard-link configuration was accepted."); } catch (IOException) { }
    Assert(await File.ReadAllTextAsync(outside) == "original", "Outside file was overwritten.");
});

await Test("Shared validation database admits independent records and deduplicates exact evidence", async () =>
{
    var folder = Path.Combine(root, "shared-database"); var first = new ValidationLedger(folder); var second = new ValidationLedger(folder);
    var evidence = new ValidationEvidence { Commit = "source-A", Tree = "tree-A", EnvironmentSha256 = "environment-A", LogSha256 = "log-A", SourceUnchanged = true };
    var ids = await Task.WhenAll(first.RecordAsync(evidence), second.RecordAsync(evidence), second.RecordAsync(new ValidationEvidence { Commit = "source-B" }));
    Assert(ids[0] == ids[1] && first.Count() == 2, "Database lost or duplicated evidence.");
});

await Test("Owned preview handles an occupied port and closes its exact listener", async () =>
{
    var project = await PracticeProject.CreateAsync(root);
    var busy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); busy.Start();
    var port = ((System.Net.IPEndPoint)busy.LocalEndpoint).Port;
    try
    {
        await using var preview = new OwnedPreview(project, port); var actual = preview.Port;
        Assert(actual != port, "Occupied port was taken from another owner.");
        using var client = new System.Net.Http.HttpClient();
        var text = await client.GetStringAsync("http://127.0.0.1:" + actual + "/README.md");
        Assert(text == await File.ReadAllTextAsync(Path.Combine(project, "README.md")), "Actual preview did not serve the project.");
        await preview.DisposeAsync();
        using var probe = new System.Net.Sockets.TcpClient();
        try { await probe.ConnectAsync("127.0.0.1", actual); throw new Exception("Preview remained active."); } catch (System.Net.Sockets.SocketException) { }
        Assert(busy.Server.IsBound, "Cleanup stopped an unrelated listener.");
    }
    finally { busy.Stop(); }
});

await Test("AppContainer permits its workspace and refuses outside writes and direct sockets", async () =>
{
    var workspace = Path.Combine(root, "isolated-work"); Directory.CreateDirectory(workspace);
    var outside = Path.Combine(root, "escaped.txt");
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    using var sandbox = new ManagedSandbox(workspace);
    var script = "Set-Content inside.txt allowed; try { Set-Content -LiteralPath " + Commands.Quote(outside) + " forbidden -ErrorAction Stop; Write-Output escaped } catch { Write-Output filesystem-blocked }; " +
        "try { $s=[Net.Sockets.TcpClient]::new(); $s.Connect('127.0.0.1'," + port + "); Write-Output connected } catch { Write-Output network-blocked }; Get-Content inside.txt";
    var result = await sandbox.RunAsync(script, Path.Combine(root, "appcontainer.log"), null, CancellationToken.None);
    var connected = listener.Pending(); listener.Stop();
    Assert(result.ExitCode == 0 && File.Exists(Path.Combine(workspace, "inside.txt")), result.Output + result.Error);
    Assert(!File.Exists(outside) && !connected && result.Output.Contains("filesystem-blocked") && result.Output.Contains("network-blocked"), "AppContainer boundary did not hold: " + result.Output + result.Error);
});

await Test("AppContainer reports native Git incompatibility instead of false success", async () =>
{
    var project = await PracticeProject.CreateAsync(root);
    using var sandbox = new ManagedSandbox(project);
    var result = await sandbox.RunAsync("git --version; git status --short; Set-Content settings.json '{\"retries\":2,\"cancellation\":false}'; git diff -- settings.json; Write-Output ('native-exit:'+$LASTEXITCODE)", Path.Combine(root, "git-sandbox.log"), null, CancellationToken.None);
    Assert(result.ExitCode != 0 && result.Error.Contains("Use agent_os_git"), result.Output + result.Error);
});

await Test("AppContainer refuses a WMI service attempt to launch an outside writer", async () =>
{
    var workspace = Path.Combine(root, "wmi-work"); Directory.CreateDirectory(workspace);
    var outside = Path.Combine(root, "wmi-escape.txt");
    var payload = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("[IO.File]::WriteAllText(" + Commands.Quote(outside) + ",'escaped'); Start-Sleep -Seconds 60"));
    using var sandbox = new ManagedSandbox(workspace);
    var command = Commands.PowerShell + " -NoProfile -EncodedCommand " + payload;
    var script = "try { $r=([wmiclass]'\\\\.\\root\\cimv2:Win32_Process').Create(" + Commands.Quote(command) + "); if($r.ReturnValue -eq 0){Write-Output ('wmi-process:'+$r.ProcessId)}else{Write-Output ('wmi-denied:'+$r.ReturnValue)} } catch {Write-Output 'wmi-denied'}";
    var control = (await Commands.RunAsync(Commands.PowerShell, ["-NoProfile", "-Command", script], root)).Checked();
    Assert(control.Contains("wmi-process:"), "The WMI positive control is unavailable: " + control);
    var controlPid = int.Parse(control.Split("wmi-process:")[1].Trim());
    using (var writer = Process.GetProcessById(controlPid))
    {
        try { await Eventually(() => File.Exists(outside)); }
        finally { if (!writer.HasExited) writer.Kill(true); }
    }
    File.Delete(outside);
    var result = await sandbox.RunAsync(script, Path.Combine(root, "wmi-sandbox.log"), null, CancellationToken.None);
    if (result.Output.Contains("wmi-process:"))
    {
        var pid = int.Parse(result.Output.Split("wmi-process:")[1].Trim());
        try { using var child = Process.GetProcessById(pid); child.Kill(true); } catch (ArgumentException) { }
        throw new Exception("WMI launched an unowned process: " + result.Output);
    }
    Assert(result.Output.Contains("wmi-denied") && !File.Exists(outside), "Outside WMI writer escaped: " + result.Output + result.Error);
});

if (filter?.Contains("Managed Codex", StringComparison.OrdinalIgnoreCase) == true)
await Test("Managed Codex changes real source through its isolated tool", async () =>
{
    var project = await PracticeProject.CreateAsync(root);
    var work = new WorkUnit { Workspace = project, Task = "Read settings.json and change retries to 2, preserving cancellation. Use agent_os_shell. Run git diff and report completion." };
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var result = await new ManagedCodexHost().Run(work, HostDiscovery.FindCodex()!, Path.Combine(root, "codex-managed.jsonl"), Console.WriteLine, timeout.Token);
    Assert(result.TurnCompleted, "Turn was not completed.");
    Assert((await File.ReadAllTextAsync(Path.Combine(project, "settings.json"))).Contains("2"), "Real source was not changed.");
});

Console.WriteLine($"{results.Count - failures}/{results.Count} passed. Evidence: {root}");
return failures == 0 ? 0 : 1;

static bool ProcessExists(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
static async Task<string> SourceIdentity()
{
    try { return (await Commands.Git(Environment.CurrentDirectory, "rev-parse", "HEAD")).Checked() + " + working tree; see source-manifest.json"; }
    catch { return "See source-manifest.json"; }
}
internal sealed class ScriptHost(bool complete = true) : IWorkHost
{
    public Task<string> Version(string executable) => Task.FromResult("deterministic process fixture (not a shipped agent host)");
    public async Task<HostResult> Run(WorkUnit work, string executable, string logPath, Action<string> output, CancellationToken cancel)
    { var code = await OwnedProcess.RunScript(work.Task, work.Workspace, logPath, output, cancel); return new(code, complete, null); }
}

internal sealed class RevisingHost : IWorkHost
{
    private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    public Task<string> Version(string executable) => Task.FromResult("automatic revision fixture");
    public async Task<HostResult> Run(WorkUnit work, string executable, string logPath, Action<string> output, CancellationToken cancel)
    {
        if (work.ParentId == null)
        {
            if (Interlocked.Increment(ref _started) == 2) _bothStarted.SetResult();
            await _bothStarted.Task.WaitAsync(cancel);
        }
        var script = work.ParentId != null
            ? "$s=Get-Content settings.json -Raw | ConvertFrom-Json; $s.cancellation=$true; $s | ConvertTo-Json | Set-Content settings.json"
            : work.Task == "retry" ? "Set-Content settings.json '{\"retries\":2,\"cancellation\":false}'"
            : "Start-Sleep -Seconds 4; Set-Content settings.json '{\"retries\":1,\"cancellation\":true}'";
        return new(await OwnedProcess.RunScript(script, work.Workspace, logPath, output, cancel), true, null);
    }
}

internal sealed class RepeatedContentionHost(string project) : IWorkHost
{
    public int Runs { get; private set; }
    public Task<string> Version(string executable) => Task.FromResult("repeated contention fixture");
    public async Task<HostResult> Run(WorkUnit work, string executable, string logPath, Action<string> output, CancellationToken cancel)
    {
        Runs++;
        // Another writer advances the real managed ref before each private candidate finishes.
        await File.WriteAllTextAsync(Path.Combine(work.Workspace, "settings.json"), "{\"retries\":" + (Runs + 10) + ",\"cancellation\":false}", cancel);
        (await Commands.Git(work.Workspace, "add", "settings.json")).Checked();
        var tree = (await Commands.Git(work.Workspace, "write-tree")).Checked();
        var commit = (await Commands.Git(work.Workspace, "-c", "user.name=fixture", "-c", "user.email=fixture@localhost", "commit-tree", tree, "-p", work.BaseCommit, "-m", "concurrent external change")).Checked();
        (await Commands.Git(project, "fetch", "--no-tags", work.Workspace, commit)).Checked();
        (await Commands.Git(project, "update-ref", ProjectRuntime.IntegratedRef, commit, work.BaseCommit)).Checked();
        await File.WriteAllTextAsync(Path.Combine(work.Workspace, "settings.json"), "{\"retries\":2,\"cancellation\":true}", cancel);
        return new(0, true, null);
    }
}


