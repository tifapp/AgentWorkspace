using AgentOS.Core;
using System.Text.Json;

try
{
    if (args.Length == 0 || args[0] == "help")
    {
        Console.WriteLine("agent-os: Codex-only Windows runtime\n  doctor\n  practice <parent-folder>\n  walkthrough <parent-folder> [data-folder]\n  run <project> <validation-command> <task>\n  status <project> [data-folder]\n  map-save <project> <json-file> [data-folder] [expected-revision]\n  map-list <project> [data-folder]\n  map-start <project> <map-id> [data-folder]\n  decide <project> <decision-id> approve|reject [data-folder]");
        return 0;
    }
    switch (args[0])
    {
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
        case "status":
        {
            await using var runtime = await ProjectRuntime.OpenAsync(args[1], args.ElementAtOrDefault(2));
            Console.WriteLine(JsonSerializer.Serialize(runtime.Snapshot, JsonFormat.Options)); return 0;
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



