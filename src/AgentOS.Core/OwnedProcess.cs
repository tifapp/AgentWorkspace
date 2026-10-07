using System.Diagnostics;
using System.Text;

namespace AgentOS.Core;

internal static class OwnedProcess
{
    public static async Task<int> RunScript(string script, string cwd, string logPath, Action<string>? output, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // Trusted bootstrap waits for stdin before running any supplied command. This closes
        // the Process.Start / AssignProcessToJobObject race without an uncontained host child.
        var gate = "$ErrorActionPreference='Stop'; [Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Text.UTF8Encoding]::new($false); if ([Console]::ReadLine() -ne 'agent-os-start') { exit 125 }; " +
                   "$scriptText=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::ReadLine())); " +
                   "$global:LASTEXITCODE=0; try { & ([ScriptBlock]::Create($scriptText)); if (!$?) {exit 1}; exit $global:LASTEXITCODE } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
        var start = new ProcessStartInfo(Commands.PowerShell) { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(gate)) }) start.ArgumentList.Add(arg);
        using var job = new WindowsJob();
        using var process = new Process { StartInfo = start };
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var logGate = new object();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        process.Start();
        try
        {
            job.Attach(process);
            using var cancel = token.Register(job.Stop);
            async Task Pump(StreamReader reader, string prefix)
            {
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        lock (logGate) log.WriteLine(prefix + line);
                        output?.Invoke(prefix + line);
                    }
                }
                catch { job.Stop(); throw; } // A failed durable callback must not strand a live producer.
            }
            stdout = Pump(process.StandardOutput, "");
            stderr = Pump(process.StandardError, "stderr: ");
            token.ThrowIfCancellationRequested();
            await process.StandardInput.WriteLineAsync("agent-os-start");
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)));
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            job.Stop(); // No background writer survives a successful root exit.
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            job.Stop();
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } } catch { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }
}
