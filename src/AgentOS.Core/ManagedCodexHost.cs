using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentOS.Core;

// Codex is the reasoning host. Its only delegated mutation tool executes in an AppContainer.
internal sealed class ManagedCodexHost : IWorkHost
{
    public async Task<string> Version(string executable)
    {
        var version = await new CodexHost().Version(executable);
        if (!string.Equals(version, "codex-cli 0.160.0", StringComparison.Ordinal)) throw new NotSupportedException("This managed integration requires the tested Codex CLI 0.160.0. Other versions require protocol and confinement validation before use.");
        return version;
    }
    public async Task<HostResult> Run(WorkUnit work, string executable, string logPath, Action<string> output, CancellationToken cancel)
    {
        var home = Path.Combine(Path.GetDirectoryName(logPath)!, "host-" + work.Id);
        Directory.CreateDirectory(home);
        var nativeWorkspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "host-native", work.Id); Directory.CreateDirectory(nativeWorkspace);
        using var sandbox = new ManagedSandbox(work.Workspace, authPath: Path.Combine(home, "auth.json"));
        var auth = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        if (File.Exists(auth)) File.Copy(auth, Path.Combine(home, "auth.json"), true);
        // Never import user hooks, plugins, MCP credentials, global instructions, or skills.
        var preferenceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        var preferences = File.Exists(preferenceFile) ? File.ReadAllLines(preferenceFile).TakeWhile(line => !line.TrimStart().StartsWith('[')).Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, "^\\s*(model|model_reasoning_effort)\\s*=\\s*\"[a-zA-Z0-9_.-]+\"\\s*$")) : [];
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), string.Join('\n', preferences) + "\napproval_policy = \"never\"\nsandbox_mode = \"read-only\"\nweb_search = \"disabled\"\n[windows]\nsandbox = \"unelevated\"\n[features]\nshell_tool = false\nunified_exec = false\n", cancel);
        var gate = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); if ([Console]::ReadLine() -ne 'GO') {exit 1}; [Console]::WriteLine('READY'); & " + Commands.Quote(executable) + " app-server --listen stdio://; exit $LASTEXITCODE";
        var info = new ProcessStartInfo(Commands.PowerShell) { WorkingDirectory = work.Workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(gate)) }) info.ArgumentList.Add(arg);
        info.Environment["CODEX_HOME"] = home;
        using var process = new Process { StartInfo = info };
        using var job = new WindowsJob();
        if (!process.Start()) throw new IOException("Could not start Codex app-server.");
        job.Attach(process);
        using var canceled = cancel.Register(job.Stop);
        var requests = new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        var writes = new SemaphoreSlim(1, 1); int serial = 0;
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var logLock = new object();
        void Log(string line) { lock (logLock) log.WriteLine(line); }
        async Task Send(object message)
        { await writes.WaitAsync(cancel); try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await process.StandardInput.FlushAsync(cancel); } finally { writes.Release(); } }
        async Task<JsonElement> Request(string method, object parameters)
        {
            var id = Interlocked.Increment(ref serial); var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); requests[id] = pending;
            await Send(new { id, method, @params = parameters });
            return await pending.Task.WaitAsync(TimeSpan.FromMinutes(2), cancel);
        }
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? thread = null, report = null, model = null;
        var tools = new ConcurrentDictionary<int, Task>();
        OwnedPreview? preview = null;
        async Task Tool(JsonElement root)
        {
            var id = root.GetProperty("id").Clone(); var p = root.GetProperty("params");
            if (root.GetProperty("method").GetString() != "item/tool/call")
            { await Send(new { id, error = new { code = -32601, message = "This operation is outside the managed delegation." } }); return; }
            var name = p.GetProperty("tool").GetString();
            string result; bool success;
            try
            {
                if (name == "agent_os_preview")
                {
                    using var ownership = await new MachineCoordinator().EnterAsync("private:" + work.Workspace, work.ShortTask, output, cancel);
                    if (preview != null) await preview.DisposeAsync();
                    preview = new OwnedPreview(work.Workspace, p.GetProperty("arguments").GetProperty("preferredPort").GetInt32());
                    result = "A fixed private source snapshot is available at http://127.0.0.1:" + preview.Port + "/ . It closes when this Codex work unit finishes or is canceled."; success = true; output(result);
                }
                else if (name == "agent_os_git")
                {
                    var arguments = p.GetProperty("arguments");
                    var operation = arguments.GetProperty("operation").GetString();
                    string[] args = operation switch
                    {
                        "status" => ["status", "--short"], "diff" => ["diff", "--no-ext-diff", "--no-textconv"],
                        "add" => ["add", "-A", "--", "."],
                        "commit" => ["-c", "user.name=agent-os", "-c", "user.email=agent-os@localhost", "commit", "-m", arguments.GetProperty("message").GetString() ?? "Private agent work"],
                        "log" => ["log", "-10", "--oneline"],
                        _ => throw new UnauthorizedAccessException("This Git operation is not delegated.")
                    };
                    using var ownership = await new MachineCoordinator().EnterAsync("private:" + work.Workspace, work.ShortTask, output, cancel);
                    PrivateGit.Prepare(work.Workspace);
                    var command = await Commands.Git(work.Workspace, args);
                    result = "Exit code: " + command.ExitCode + "\n" + command.Output + command.Error; success = command.ExitCode == 0;
                    output(result);
                }
                else
                {
                if (name != "agent_os_shell") throw new UnauthorizedAccessException("Unsupported tool.");
                var script = p.GetProperty("arguments").GetProperty("script").GetString() ?? "";
                using var ownership = await new MachineCoordinator().EnterAsync("private:" + work.Workspace, work.ShortTask, output, cancel);
                output("Running a command in the private workspace.");
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel); limit.CancelAfter(TimeSpan.FromMinutes(2));
                var command = await sandbox.RunAsync(script, Path.Combine(home, "command-" + Guid.NewGuid().ToString("N") + ".log"), output, limit.Token);
                result = "Exit code: " + command.ExitCode + "\n" + command.Output + command.Error; success = command.ExitCode == 0;
                }
            }
            catch (Exception e) when (!cancel.IsCancellationRequested) { result = e.Message; success = false; }
            await Send(new { id, result = new { success, contentItems = new[] { new { type = "inputText", text = result } } } });
        }
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(cancel) is { } line)
                {
                    Log(line); if (line == "READY") { ready.TrySetResult(); continue; } using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                    if (r.TryGetProperty("id", out var id) && !r.TryGetProperty("method", out _))
                    {
                        if (requests.TryRemove(id.GetInt32(), out var pending))
                        { if (r.TryGetProperty("error", out var error)) pending.TrySetException(new IOException(error.ToString())); else pending.TrySetResult(r.GetProperty("result").Clone()); }
                    }
                    else if (r.TryGetProperty("method", out var method))
                    {
                        if (r.TryGetProperty("id", out _)) { var key = Interlocked.Increment(ref serial); tools[key] = Tool(r.Clone()); continue; }
                        if (!r.TryGetProperty("params", out var p)) continue;
                        if (method.GetString() == "item/completed" && p.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var kind) && kind.GetString() == "agentMessage")
                        { report = item.GetProperty("text").GetString(); output(report ?? ""); }
                        if (method.GetString() == "turn/completed") finished.TrySetResult(p.GetProperty("turn").GetProperty("status").GetString() == "completed");
                    }
                }
                finished.TrySetException(new IOException("Codex exited without confirming completion."));
            }
            catch (Exception e) { finished.TrySetException(e); foreach (var pending in requests.Values) pending.TrySetException(e); }
        }, CancellationToken.None);
        var errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync(cancel) is { } line) Log("stderr: " + line); }, CancellationToken.None);
        try
        {
            await process.StandardInput.WriteLineAsync("GO"); await process.StandardInput.FlushAsync(cancel);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancel);
            await Request("initialize", new { clientInfo = new { name = "agent_os", version = "0.2.0" }, capabilities = new { experimentalApi = true } });
            await Send(new { method = "initialized" });
            var started = await Request("thread/start", new { cwd = nativeWorkspace, approvalPolicy = "never", sandbox = "read-only", ephemeral = true,
                config = new Dictionary<string, object> { ["features.shell_tool"] = false, ["features.unified_exec"] = false,
                    ["features.hooks"] = false, ["features.plugins"] = false, ["features.apps"] = false,
                    ["features.multi_agent"] = false, ["features.multi_agent_v2"] = false,
                    ["features.image_generation"] = false, ["features.view_image"] = false, ["web_search"] = "disabled" },
                developerInstructions = "You are authorized to MODIFY the delegated project through agent_os_shell and agent_os_git. The native Codex sandbox is read-only intentionally: it protects the host configuration, NOT the separately delegated project. Your managed tools execute in another, writable private project workspace. Do not refuse an authorized project edit because native tools are read-only. Use agent_os_shell for file edits, shell and PowerShell tests. Use agent_os_git for Git status/diff/add/commit/log. Native Git is incompatible with AppContainer on this Windows version and is blocked. Relative paths work with PowerShell providers; .NET APIs require [Environment]::CurrentDirectory, not the virtual Work: drive. Every managed command has no network access and cannot write outside its private workspace. Native execution tools are disabled. Coordination is automatic; do not maintain registry entries. Read current files before editing; complete the task. Runtime validates and integrates after you finish.",
                dynamicTools = new object[] { new { type = "function", name = "agent_os_shell", description = "Execute ordinary PowerShell files, shell and PowerShell tests in the private Windows workspace. No network or outside writes. Children end with this command. Maximum duration 120 seconds. For Git use agent_os_git.", inputSchema = new { type = "object", properties = new { script = new { type = "string" } }, required = new[] { "script" }, additionalProperties = false } },
                    new { type = "function", name = "agent_os_git", description = "Ordinary private Git status, diff, add all changes, commit with message, or log. Remote operations and configuration changes are not delegated.", inputSchema = new { type = "object", properties = new { operation = new { type = "string", @enum = new[] { "status", "diff", "add", "commit", "log" } }, message = new { type = "string" } }, required = new[] { "operation" }, additionalProperties = false } },
                    new { type = "function", name = "agent_os_preview", description = "Start a task-owned loopback preview of a fixed private source snapshot. If the preferred port is occupied, choose an available port. Dot files and oversized files are excluded; source is served as plain text. Closes on completion or cancellation.", inputSchema = new { type = "object", properties = new { preferredPort = new { type = "integer", minimum = 0, maximum = 65535 } }, required = new[] { "preferredPort" }, additionalProperties = false } } } });
            thread = started.GetProperty("thread").GetProperty("id").GetString();
            if (started.TryGetProperty("model", out var actualModel)) model = actualModel.GetString();
            await Request("turn/start", new { threadId = thread, input = new[] { new { type = "text", text = work.Task } } });
            var ok = await finished.Task.WaitAsync(cancel);
            await Task.WhenAll(tools.Values);
            return new(ok ? 0 : 1, ok, thread, report, model);
        }
        finally
        {
            job.Stop(); try { await Task.WhenAll(reader, errors).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (preview != null) { await preview.DisposeAsync(); output("The task-owned preview has closed."); }
            // Auth is never retained as task evidence.
            var copiedAuth = Path.Combine(home, "auth.json"); if (File.Exists(copiedAuth)) File.Delete(copiedAuth);
        }
    }
}
