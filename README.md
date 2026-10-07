# agent os for Windows

A native Windows workspace for **Codex**, built with C# / .NET 10 LTS and WinUI 3 (Windows App SDK 2.5.1). Start concurrent Codex tasks, inspect private candidates, validate combined changes, integrate them into a managed Git branch, and make scoped local release decisions.

## Product objective

**Outperform and supersede the resource-checkin skill, making it unnecessary for supported Codex work.** Agents should use ordinary development tools while agent-os automatically handles resource ownership, contention, waiting, revalidation, lifecycle and recovery. Maximize valid autonomous progress with as few interruptions as possible.

Supported work now uses automatic effect admission, command isolation and process ownership. Agents maintain no registry entries. The installed skill requirement and execution have been retired through the evidence-gated migration. Host-protected original files remain on disk; see [scope and retirement evidence](docs/retirement.md). Unsupported external operations remain explicit gaps.

## Run

The current portable build is `artifacts/release/agent-os-retirement-win-x64/AgentOS.exe`; the distributable is `artifacts/agent-os-retirement-win-x64.zip`. Extract the entire ZIP before launching. It includes .NET and the Windows App SDK; no development SDK or app registration is required. It is an unsigned local build.

Requirements: Windows x64, build 19041 or later; Git for Windows; installed and authenticated **Codex CLI 0.160.0**. Run `codex login` in your terminal first. The app's Setup page checks prerequisites and shows mediation coverage. Codex is the sole agent host. Your configured model/account are used; live tasks consume normal Codex usage.

1. Open a **local Git project with at least one commit**, or click **Create practice project** in Setup.
2. Save a PowerShell validation command, such as `& .\Validate.ps1`. It runs in the command sandbox against the complete combined candidate and must fail when checks fail. Arbitrary native SDKs, including `dotnet test`, are not a verified managed surface in this release.
3. In Workspace, describe a task and select **Start Codex task**. Launch another at any time. Ordinary file, shell, test and local Git work happen in private clones.
4. Inspect the task map, revision threads, Codex reports, changed paths, diff, transcripts and validation evidence. Completed means validated and integrated into **agent-os/integrated**, not into the currently checked-out branch. Uncommitted source files are not task inputs.
5. Use **Prepare release decision** for a tested commit. Review its exact scope and evidence, then approve or decline the local release tag. Independent tasks continue while the decision is pending.
6. Cancel active work to stop its owned processes. After inspecting retained output, clean its private files if desired. Reopen the app to recover saved state.

On a fresh practice project, **Run concurrency check** launches three real Codex tasks, exercises conflicting and independent candidates, asks Codex to revise stale work, and leaves a scoped decision ready for review. This uses real files, Git commits, processes, validation and host sessions.

**Coverage:** managed PowerShell, private Git, shared publication, owned process trees, loopback previews and the shared evidence database. External services and unmanaged programs are not mediated. Read [the enforcement and recovery contract](docs/guarantees.md).

The packaged `cli/AgentOS.Cli.exe` supports `doctor`, `practice`, `run`, `status` and `walkthrough`. A `run` launch attaches to an already open project's desktop runtime, retaining its validation policy and evidence. Example: `& .\cli\AgentOS.Cli.exe run 'C:\project' '& .\Validate.ps1' 'Describe the change'`.

## Build and verify

```powershell
# If .NET SDK 10.0.401 is not installed, install it locally in this repo:
.\scripts\bootstrap.ps1

# Build, run the integration suite, and create the portable ZIP:
.\scripts\build.ps1 -Test -Publish

# Run only the integration suite (uses real Git and Windows processes; no model calls):
.\.tools\dotnet\dotnet.exe run --project tests/AgentOS.Tests -- artifacts/tests/manual

# Exercise real Codex concurrency through the same runtime as the app:
.\.tools\dotnet\dotnet.exe run --project src/AgentOS.Cli -- walkthrough artifacts/live artifacts/live-state
```

Builds require network access to Microsoft's .NET and NuGet feeds. `global.json` pins the SDK. The app packages both managed and native runtime dependencies. Keep every file from the release folder together. See [testing and validation](docs/testing.md) and [recorded validation](docs/validation-report.md).

## Source layout

| Component | Purpose |
| --- | --- |
| `src/AgentOS.Core` | Durable runtime, Codex stream adapter, Git publication, validation, decisions, Windows process ownership |
| `src/AgentOS.App` | WinUI setup, task workspace, decision review and evidence interface |
| `src/AgentOS.Cli` | Prerequisite diagnostics and reproducible runtime workflows |
| `tests/AgentOS.Tests` | Executable integration suite using real Git repositories and process trees |
| `scripts` | Build, bootstrap, source hashing and desktop UI automation |

The repository began as a README only. This is a new Windows runtime; it does not claim compatibility with the macOS daemon or its schema-14 registry. The supplied North Star, Rebuild Guide, Workflow Edge Cases and Task Map States informed the implementation. See [reference cases and UI mapping](docs/reference-coverage.md) and the [implementation plan](docs/implementation-plan.md).

## Current source revision

Draft task-map source, desktop capture, map review, durable interactions, and external-effect review controls have been added since the last compiled release. See [integration status](docs/integration-status.md) for verification limits. Phone work is deferred.

Desktop capture, map review, interaction, and effect controls are described in [desktop interaction controls](docs/desktop-interactions.md).


