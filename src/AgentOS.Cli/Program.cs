using System.Diagnostics;
using AgentOS.Core;
using System.Text.Json;

try
{
    if (args.Length == 0 || args[0] == "help")
    {
        Console.WriteLine(@"agent-os: Codex-only Windows runtime
  update-health <project>
  update-drain <project> <sha256> <version> <architecture> <install-root> <package> <signer>
  update-ready <project> <token>
  update-exit <project> <token>
  update-cancel-map <project> <map-id>
  update-handshake <protocol> <maximum-schema> <assembly-sha256>
  update-restart <project> <data-root>
  doctor
  practice <parent-folder>
  walkthrough <parent-folder> [data-folder]
  run <project> <validation-command> <task>
  status <project> [data-folder]
  conflicts <project> [work-id] [data-folder]
  resume-conflict <project> <work-id> [data-folder]
  Conflict responses are free form in the same Codex thread. A final answer does not satisfy an owed response.
  Responses park the retained candidate until explicit resolution or abandonment; no outcome is chosen automatically.
  map-save <project> <json-file> [data-folder] [expected-revision]
  map-list <project> [data-folder]
  map-start <project> <map-id> [data-folder]
  map-start-selected <project> <map-id> <expected-revision> <task-id[,task-id...]> [data-folder]
  submit <project> <task> [request-id]
  reply-after <project> <work-id> <text> [request-id]
  peer <project> <work-id> <target-work-id> <question> [deadline]
  ack-peer <project> <work-id> <request-id> <response>
  handoff-peer <project> <work-id> <request-id> <new-work-id>
  resolve-wait <project> <work-id> <wait-id> <resolution>
  inspect <project> <work-id>
  sdk-pending <project> <work-id> [data-folder]
  sdk-reconcile <project> <work-id> <operation-id> [data-folder]
  cancel <project> <work-id>
  interactions <project> [work-id]
  steer <project> <work-id> <text>
  reply <project> <work-id> <clarification-id> <text>
  followup <project> <work-id> <proposal-id>
  reject-followup <project> <work-id> <proposal-id> <reason>
  resolve <project> <work-id> <obligation-id> <text>
  wait <project> <work-id> task|message|decision|resource <target-id> [deadline]
  cancel-wait <project> <work-id> <wait-id>
  decide <project> <decision-id> approve|reject [data-folder]");
        return 0;
    }
    switch (args[0])
    {
        case "update-health": Console.WriteLine(JsonSerializer.Serialize(await ProjectClient.UpdateHealthAsync(args[1]) ?? throw new IOException("Owning runtime unavailable."), JsonFormat.Options)); return 0;
        case "update-drain":
        {
            var scope = new UpdateScope(args[2], args[3], args[4], Path.GetFullPath(args[5]), Path.GetFullPath(args[1]), Path.GetFullPath(args[6]), args[7]);
            Console.WriteLine(JsonSerializer.Serialize(await ProjectClient.BeginUpdateDrainAsync(args[1], scope) ?? throw new IOException("Owning runtime unavailable."), JsonFormat.Options)); return 0;
        }
        case "update-ready":
        {
            var ready = await ProjectClient.UpdateReadyAsync(args[1], args[2]) ?? throw new IOException("Owning runtime unavailable.");
            Console.WriteLine(JsonSerializer.Serialize(ready, JsonFormat.Options)); return ready.Ready ? 0 : 2;
        }
        case "update-exit": if (!await ProjectClient.UpdateExitAsync(args[1], args[2])) throw new IOException("Owning runtime unavailable."); Console.WriteLine("Cooperative exit requested."); return 0;
        case "update-cancel-map": if (!await ProjectClient.CancelPendingMapForUpdateAsync(args[1],args[2])) throw new IOException("Owning runtime unavailable."); Console.WriteLine("Unlaunched map tasks canceled."); return 0;
        case "update-handshake":
        {
            var h = RuntimeUpdate.Health();
            if (h.Version != int.Parse(args[1]) || h.StateSchema > int.Parse(args[2]) || !string.Equals(h.AssemblySha256, args[3], StringComparison.OrdinalIgnoreCase) || !new[] { "scoped-drain", "cooperative-exit", "schema3", "signed-msix-handshake" }.All(h.Capabilities.Contains)) throw new InvalidOperationException("Installed CLI protocol, schema, assembly hash or capabilities incompatible.");
            Console.WriteLine(JsonSerializer.Serialize(h, JsonFormat.Options)); return 0;
        }
        case "update-restart":
        {
            var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "AgentOS.exe"));
            if (!File.Exists(app)) throw new FileNotFoundException("Packaged runtime entrypoint missing.", app);
            var launch = new ProcessStartInfo(app) { UseShellExecute = true };
            launch.ArgumentList.Add("--project"); launch.ArgumentList.Add(Path.GetFullPath(args[1]));
            launch.ArgumentList.Add("--data-root"); launch.ArgumentList.Add(Path.GetFullPath(args[2]));
            if (Process.Start(launch) == null) throw new IOException("Packaged runtime launch refused.");
            Console.WriteLine("Packaged runtime launch accepted."); return 0;
        }
        case "doctor":
            var checks = await HostDiscovery.CheckAsync();
            Console.WriteLine(JsonSerializer.Serialize(checks, JsonFormat.Options));
            return checks.All(x => x.Ready) ? 0 : 1;
        case "practice": Console.WriteLine(await PracticeProject.CreateAsync(args[1])); return 0;
        case "walkthrough":
        {
            var path = await PracticeProject.CreateAsync(args[1]);
            Console.WriteLine("Project: " + path);
            await using var runtime = await ProjectRuntime.OpenAsync(path, args.ElementAtOrDefault(2));
            Console.WriteLine("Evidence: " + runtime.DataDirectory);
            await PracticeProject.RunWalkthroughAsync(runtime, Console.WriteLine);
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot, JsonFormat.Options));
            return 0;
        }
        case "run":
        {
            var request = Guid.NewGuid().ToString("N");
            var remote = await ProjectClient.TryStartAsync(args[1], args[3], request, args[2]);
            if (remote != null)
            {
                var remoteId = remote.Id;
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = ProjectClient.CancelAsync(args[1], remoteId); };
                while (remote.IsActive)
                {
                    await Task.Delay(250);
                    remote = await ProjectClient.InspectAsync(args[1], remoteId) ?? throw new IOException("The owning runtime stopped. Reopen the project to reconcile the retained task; no task was replayed.");
                }
                Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return remote.Status == WorkStatus.Completed ? 0 : 1;
            }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            runtime.Configure(args[2]);
            var id = await runtime.StartAsync(args[3], externalRequestId: request);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; runtime.Cancel(id); };
            await runtime.WaitForIdleAsync();
            var work = runtime.Snapshot.Work.Single(w => w.Id == id);
            Console.WriteLine(JsonSerializer.Serialize(work, JsonFormat.Options));
            return work.Status == WorkStatus.Completed ? 0 : 1;
        }
        case "sdk-pending":
        {
            await using var runtime=await ProjectRuntime.OpenAsync(args[1],args.ElementAtOrDefault(3));Console.WriteLine(runtime.PendingSdkOperationId(args[2])??"No pending SDK operation.");return 0;
        }
        case "sdk-reconcile":
        {
            await using var runtime=await ProjectRuntime.OpenAsync(args[1],args.ElementAtOrDefault(4));var receipt=await runtime.ReconcileSdkAsync(args[2],args[3]);Console.WriteLine(JsonSerializer.Serialize(receipt,JsonFormat.Options));return receipt.ShutdownConfirmed&&receipt.TrustedCollector?0:1;
        }
        case "status":
        {
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(2));
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot, JsonFormat.Options)); return 0;
        }
        case "conflicts":
        {
            var remote = await ProjectClient.ConflictsAsync(args[1], args.ElementAtOrDefault(2));
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(3));
            var state = runtime.Snapshot; var workId = args.ElementAtOrDefault(2);
            Console.WriteLine(JsonSerializer.Serialize(new ConflictInspection(state.Conflicts.Where(x => workId == null || x.WorkId == workId).ToArray(), state.Escalations.Where(x => workId == null || x.WorkId == workId).ToArray()), JsonFormat.Options)); return 0;
        }
        case "resume-conflict":
        {
            var remote = await ProjectClient.ResumeConflictAsync(args[1], args[2]);
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(3));
            await runtime.ResumeConflictAsync(args[2]);
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Work.Single(x => x.Id == args[2]), JsonFormat.Options)); return 0;
        }
        case "submit":
        {
            var request = args.ElementAtOrDefault(3) ?? Guid.NewGuid().ToString("N");
            var remote = await ProjectClient.SubmitAsync(args[1], args[2], request);
            if (remote != null) { Console.WriteLine(remote); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            var id = await runtime.StartAsync(args[2], externalRequestId: request); Console.WriteLine(id); await runtime.WaitForIdleAsync(); return 0;
        }
        case "reply-after":
        {
            var request = args.ElementAtOrDefault(4) ?? Guid.NewGuid().ToString("N");
            var remote = await ProjectClient.ReplyAfterAsync(args[1], args[2], args[3], request);
            if (remote != null) { Console.WriteLine(remote); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            var id = await runtime.ReplyAfterCompletionAsync(args[2], args[3], request); Console.WriteLine(id); await runtime.WaitForIdleAsync(); return 0;
        }
        case "peer":
        {
            var remote = await ProjectClient.AskPeerAsync(args[1], args[2], args[3], args[4], args.Length > 5 ? DateTimeOffset.Parse(args[5]) : null);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0;
        }
        case "ack-peer":
        {
            var remote = await ProjectClient.AckPeerAsync(args[1], args[2], args[3], args[4]);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0;
        }
        case "handoff-peer":
        {
            var remote = await ProjectClient.HandoffPeerAsync(args[1], args[2], args[3], args[4]);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0;
        }
        case "resolve-wait":
        {
            var remote = await ProjectClient.ResolveWaitAsync(args[1], args[2], args[3], args[4]);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0;
        }
        case "inspect":
        {
            var remote = await ProjectClient.InspectAsync(args[1], args[2]);
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Work.Single(x => x.Id == args[2]), JsonFormat.Options)); return 0;
        }
        case "cancel":
        {
            var remote = await ProjectClient.CancelAsync(args[1], args[2]);
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            runtime.Cancel(args[2]); await runtime.WaitForIdleAsync();
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Work.Single(x => x.Id == args[2]), JsonFormat.Options)); return 0;
        }
        case "interactions":
        {
            var remote = await ProjectClient.InteractionsAsync(args[1], args.ElementAtOrDefault(2));
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(runtime.InspectInteractions(args.ElementAtOrDefault(2)), JsonFormat.Options)); return 0;
        }
        case "steer":
        {
            var remote = await ProjectClient.SteerAsync(args[1], args[2], args[3]);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0;
        }
        case "reply":
        {
            var remote = await ProjectClient.ReplyAsync(args[1], args[2], args[3], args[4]);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0;
        }
        case "followup":
        {
            var started = await ProjectClient.AcceptFollowupAsync(args[1], args[2], args[3]);
            if (started != null) { Console.WriteLine(started); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            var id = await runtime.AcceptFollowup(args[2], args[3]); Console.WriteLine(id); await runtime.WaitForIdleAsync(); return 0;
        }
        case "reject-followup":
        {
            var remote=await ProjectClient.RejectFollowupAsync(args[1],args[2],args[3],args[4]);
            if(remote!=null){Console.WriteLine(JsonSerializer.Serialize(remote,JsonFormat.Options));return 0;}
            await using var runtime=await ProjectRuntime.OpenAsync(args[1]);Console.WriteLine(JsonSerializer.Serialize(runtime.RejectFollowup(args[2],args[3],args[4]),JsonFormat.Options));return 0;
        }
        case "resolve":
        {
            var remote = await ProjectClient.ResolveAsync(args[1], args[2], args[3], args[4]);
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(runtime.ResolveObligation(args[2], args[3], args[4]), JsonFormat.Options)); return 0;
        }
        case "wait":
        {
            var kind = Enum.Parse<WaitKind>(args[3], true);
            var deadline = args.Length > 5 ? DateTimeOffset.Parse(args[5]) : (DateTimeOffset?)null;
            var remote = await ProjectClient.WaitAsync(args[1], args[2], kind, args[4], deadline);
            if (remote == null) throw new InvalidOperationException("The owning runtime is not running.");
            while (remote.Status == InteractionStatus.Pending)
            {
                await Task.Delay(250);
                var current = await ProjectClient.InteractionsAsync(args[1], args[2]) ?? throw new IOException("The owning runtime stopped; inspect the durable wait after reopening.");
                remote = current.Single(x => x.Id == remote.Id);
            }
            Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return remote.Status == InteractionStatus.Resolved ? 0 : 1;
        }
        case "cancel-wait":
        {
            var remote = await ProjectClient.CancelWaitAsync(args[1], args[2], args[3]);
            if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(runtime.CancelWait(args[2], args[3]), JsonFormat.Options)); return 0;
        }
        case "decide":
        {
            if (args[3] is not ("approve" or "reject")) throw new ArgumentException("Choose approve or reject.");
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(4));
            await runtime.DecideAsync(args[2], args[3] == "approve");
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Decisions, JsonFormat.Options)); return 0;
        }
        case "map-save":
        {
            var map = JsonSerializer.Deserialize<TaskMap>(await File.ReadAllTextAsync(args[2]), JsonFormat.Options) ?? throw new ArgumentException("The map JSON is empty.");
            var revision = args.Length > 4 ? long.Parse(args[4]) : (long?)null;
            var remoteMap = await ProjectClient.SaveMapAsync(args[1], map, revision);
            if (remoteMap != null) { Console.WriteLine(JsonSerializer.Serialize(remoteMap, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(3));
            var id = runtime.SaveDraftMap(map, revision);
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Maps.Single(m => m.Id == id), JsonFormat.Options)); return 0;
        }
        case "map-list":
        {
            var remoteMaps = await ProjectClient.MapsAsync(args[1]);
            if (remoteMaps != null) { Console.WriteLine(JsonSerializer.Serialize(remoteMaps, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(2));
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Maps, JsonFormat.Options)); return 0;
        }
        case "map-start-selected":
        {
            var revision=long.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture);var ids=args[4].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            var remote=await ProjectClient.StartMapAsync(args[1],args[2],ids,revision);if(remote!=null){Console.WriteLine(JsonSerializer.Serialize(remote,JsonFormat.Options));return 0;}
            await using var runtime=await ProjectRuntime.OpenAsync(args[1],args.ElementAtOrDefault(5));var started=await runtime.StartSelectedMapTasksAsync(args[2],ids,revision);Console.WriteLine(JsonSerializer.Serialize(started,JsonFormat.Options));await runtime.WaitForIdleAsync();return 0;
        }
        case "map-start":
        {
            var remoteStarted = await ProjectClient.StartMapAsync(args[1], args[2]);
            if (remoteStarted != null) { Console.WriteLine(JsonSerializer.Serialize(remoteStarted, JsonFormat.Options)); return 0; }
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(3));
            var started = await runtime.StartSelectedMapTasksAsync(args[2]);
            Console.WriteLine(JsonSerializer.Serialize(started, JsonFormat.Options));
            await runtime.WaitForIdleAsync();
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot.Maps.Single(m => m.Id == args[2]), JsonFormat.Options)); return 0;
        }
        default: throw new ArgumentException("Unknown command. Run help.");
    }
}
catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }

