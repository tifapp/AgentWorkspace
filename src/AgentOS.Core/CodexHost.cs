using System.Text.Json;

namespace AgentOS.Core;

// Test injection is internal; Codex is the only user-selectable host.
internal interface IWorkHost
{
    Task<string> Version(string executable);
    Task<HostResult> Run(WorkUnit work, string executable, string logPath, Action<string> output, CancellationToken cancel);
}

internal sealed class CodexHost
{
    public async Task<string> Version(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new FileNotFoundException("Codex CLI was not found. Install it, run codex login, and reopen the project.");
        var result = await Commands.RunAsync(Commands.PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "& " + Commands.Quote(executable) + " --version; exit $LASTEXITCODE"], Environment.CurrentDirectory);
        return result.Checked();
    }
}

public static class HostDiscovery
{
    public static string? FindCodex()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "codex.exe", "codex.cmd" })
            { var path = Path.Combine(dir.Trim('"'), name); if (File.Exists(path)) return path; }
        return null;
    }
    public static async Task<List<Prerequisite>> CheckAsync()
    {
        var result = new List<Prerequisite> { new("Windows", Environment.OSVersion.Version.Build >= 19041, Environment.OSVersion.VersionString),
            new(".NET", true, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            new("PowerShell", File.Exists(Commands.PowerShell), "Used to supervise Codex and run the project's validation command.") };
        try { var git = await Commands.Git(Environment.CurrentDirectory, "--version"); result.Add(new("Git", git.ExitCode == 0, git.Output.Trim())); }
        catch (Exception e) { result.Add(new("Git", false, "Install Git for Windows and restart agent-os. " + e.Message)); }
        var codex = FindCodex();
        if (codex == null) result.Add(new("Codex", false, "Install the Codex CLI, run codex login in a terminal, then restart agent-os."));
        else
        {
            var temp = Path.Combine(Path.GetTempPath(), "agent-os-check-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var lines = new List<string>();
                var code = await OwnedProcess.RunScript("& " + Commands.Quote(codex) + " --version; & " + Commands.Quote(codex) + " login status; exit $LASTEXITCODE", Environment.CurrentDirectory, temp, s => { lock (lines) lines.Add(s); }, timeout.Token);
                var supported = lines.Any(line => line.Trim() == "codex-cli 0.160.0");
                result.Add(new("Codex", code == 0 && supported, string.Join("\n", lines) + (supported ? "" : "\nInstall Codex CLI 0.160.0; other host versions are not validated by this release.")));
            }
            catch (Exception e) { result.Add(new("Codex", false, e.Message)); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return result;
    }
}
