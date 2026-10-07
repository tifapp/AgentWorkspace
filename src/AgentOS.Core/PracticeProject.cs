namespace AgentOS.Core;

public static class PracticeProject
{
    public const string ValidateCommand = "& .\\Validate.ps1";
    public static async Task<string> CreateAsync(string parent)
    {
        var path = Path.Combine(Path.GetFullPath(parent), "coordination-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{\n  \"retries\": 1,\n  \"cancellation\": false\n}\n");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Coordination practice\n\nA local project for real Codex work units.\n");
        await File.WriteAllTextAsync(Path.Combine(path, "Validate.ps1"), """
            $ErrorActionPreference='Stop'
            $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'settings.json') -Raw | ConvertFrom-Json
            if ($settings.retries -lt 1 -or $settings.retries -gt 3) { throw 'retries must be between 1 and 3' }
            if ($settings.cancellation -isnot [bool]) { throw 'cancellation must be a Boolean' }
            if (!(Test-Path (Join-Path $PSScriptRoot 'README.md'))) { throw 'README is missing' }
            Start-Sleep -Seconds 2
            Write-Output ('PASS: retries=' + $settings.retries + '; cancellation=' + $settings.cancellation)
            """);
        (await Commands.Git(path, "init", "-b", "main")).Checked();
        (await Commands.Git(path, "add", ".")).Checked();
        (await Commands.Git(path, "-c", "user.name=agent-os", "-c", "user.email=agent-os@localhost", "commit", "-m", "Create coordination practice project")).Checked();
        return path;
    }

    public static readonly string[] Tasks =
    [
        "Edit only settings.json to change retries from 1 to 2. Keep cancellation unchanged. Run & .\\Validate.ps1 using agent_os_shell, and git diff using agent_os_git. Do not change the validation script. Finish after this small change.",
        "Edit only settings.json to set cancellation to true. Preserve its current retries value. Run & .\\Validate.ps1 using agent_os_shell, and git diff using agent_os_git. Do not change the validation script. Finish after this small change.",
        "Edit only README.md by appending a short section titled Local validation explaining that & .\\Validate.ps1 checks settings.json. Do not edit any other file. Run that validation command using agent_os_shell and git diff using agent_os_git, then finish."
    ];

    // Uses exactly the same public runtime and Codex adapter as manually launched work.
    public static async Task RunWalkthroughAsync(ProjectRuntime runtime, Action<string>? progress = null)
    {
        runtime.Configure(ValidateCommand);
        var ids = new List<string>();
        foreach (var task in Tasks) ids.Add(await runtime.StartAsync(task, false));
        progress?.Invoke("Three real Codex tasks are running from the same shared base.");
        await runtime.WaitForIdleAsync();
        var state = runtime.Snapshot;
        if (ids.Any(id => state.Work.Single(w => w.Id == id).Status != WorkStatus.Private))
            throw new IOException("A Codex task did not prepare a candidate. Inspect its transcript before continuing.");
        var first = runtime.IntegrateAsync(ids[0]);
        // First admission is observable before requesting the conflicting and independent candidate.
        while (runtime.Snapshot.Work.Single(w => w.Id == ids[0]).Status == WorkStatus.Private) await Task.Delay(20);
        var second = runtime.IntegrateAsync(ids[1]);
        var third = runtime.IntegrateAsync(ids[2]);
        await Task.WhenAll(first, second, third);
        state = runtime.Snapshot;
        if (state.Work.Single(w => w.Id == ids[0]).Status != WorkStatus.Completed || state.Work.Single(w => w.Id == ids[1]).Status != WorkStatus.Stale || state.Work.Single(w => w.Id == ids[2]).Status != WorkStatus.Completed)
            throw new IOException("The contention result did not meet the expected completed / stale / independent-completed outcomes. Inspect the evidence.");
        await runtime.RequestReleaseAsync(ids[2]);
        progress?.Invoke("The conflicting candidate was refused as stale. Independent documentation integrated. A scoped release decision is pending while Codex revises the stale task.");
        var revised = await runtime.ReviseAsync(ids[1]);
        await runtime.WaitForIdleAsync();
        await runtime.IntegrateAsync(revised);
        if (runtime.Snapshot.Work.Single(w => w.Id == revised).Status != WorkStatus.Completed)
            throw new IOException("Codex's revision did not integrate. Inspect its evidence.");
        progress?.Invoke("Revised work is integrated and validated. The earlier local release decision remains scoped to its original candidate.");
    }
}
