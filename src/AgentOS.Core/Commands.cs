using System.Diagnostics;
using System.Text;

namespace AgentOS.Core;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public string Checked() => ExitCode == 0 ? Output.TrimEnd('\r', '\n') : throw new IOException(Error.Trim() is { Length: > 0 } e ? e : Output.Trim());
}

public static class Commands
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, string cwd,
        CancellationToken cancel = default, string? input = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Runtime Git operations must never invoke project hooks, pagers or a credential prompt.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_COUNT"] = "4";
        start.Environment["GIT_CONFIG_KEY_0"] = "core.hooksPath";
        start.Environment["GIT_CONFIG_VALUE_0"] = "NUL";
        start.Environment["GIT_CONFIG_KEY_1"] = "core.autocrlf";
        start.Environment["GIT_CONFIG_VALUE_1"] = "false";
        start.Environment["GIT_CONFIG_KEY_2"] = "protocol.file.allow";
        start.Environment["GIT_CONFIG_VALUE_2"] = "always";
        start.Environment["GIT_CONFIG_KEY_3"] = "core.fsmonitor";
        start.Environment["GIT_CONFIG_VALUE_3"] = "false";
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            start.Environment.Remove(key);
        if (environment != null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        return await SuspendedCommand.Run(start, input, timeout.Token);
    }
    public static Task<CommandResult> Git(string cwd, params string[] args) => RunAsync("git", args, cwd);
    public static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
