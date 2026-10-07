# Resource-checkin retirement

## Installed result

On 2026-10-07 the user authorized retirement. The global `~/.codex/AGENTS.md` now directs supported work through agent-os and removes check-in, renewal, resource declarations and checkout. The machine registry contains unsupported schema 99. Both already-loaded and newly invoked old clients reject admission without changing that registry.

The installed directory has host-managed Windows permissions that denied moving it. The first attempt rolled back. The successful cutover preserved those permissions and archived a copy at:

`C:\Users\seani\.codex\retired-skills\resource-checkin-20261007-083314`

**Original skill files remain on disk and may remain in a skill catalogue.** They are retired operationally through the replacement instructions and frozen registry. Physical removal needs an owner with permission to remove that directory. No ACL bypass was performed. No live legacy task was erased.

The archive contains the skill, original instructions, pre-cutover and drained registries, and `receipt.json`. The project receipt is `artifacts/retirement/retirement-receipt.json`; `cutover-verification.json` proves the installed legacy client exited with failure and left the registry unchanged. The isolated `migration-fixture/result.json` also proves other work prevents cutover and remains intact.

## Replacement evidence

All three acceptance receipts identify Core SHA-256 `FAD953C3941E958A51156756674402D02C1440F9AAC62A5165246631FC560FE7`:

- `tests-final-acceptance/results.json`: 34/34 integration checks.
- `desktop-final-acceptance/result.json`: real Codex tasks, conflicting mutation, independent progress, stale revision, exact release approval, cancellation and restart.
- `comparison-final-acceptance/result.json`: 11 matched repetitions; median managed 136.47 ms, automated legacy wrapper 222.09 ms, manual registry commands 312.54 ms. This measures coordination overhead, not whole-task performance or model cost.

Receipts are under `artifacts/retirement`. Managed agents perform zero registry maintenance and supply no resource declarations. The automated legacy wrapper also needs no manual renewal; it still requires the caller to specify resources and does not confine arbitrary file effects.

## Run without the skill

Extract the complete current ZIP. Start `AgentOS.exe`, select a local committed Git project, save a PowerShell validation command and start a Codex task. The packaged CLI is `cli/AgentOS.Cli.exe`:

```powershell
& .\cli\AgentOS.Cli.exe doctor
& .\cli\AgentOS.Cli.exe run 'C:\project' '& .\Validate.ps1' 'Describe the change'
```

An open desktop owns its project's runtime; CLI launches attach through its broker without asking the user to close the app. A caller cannot change that runtime's validation policy. Results and evidence remain associated with the owning project and source versions.

## Scope and recovery

Replacement covers managed PowerShell, private Git, validated local Git publication, owned processes, loopback previews and the runtime evidence database for this Windows user. External services, deployments, arbitrary native SDKs and unmanaged writers are not automatically mediated. See [guarantees](guarantees.md).

To roll back, first stop all agent-os tasks and runtimes and inspect uncertain outcomes. An authorized owner can restore the archived original instructions and **drained** registry, then restore the skill directory if it was moved. Do not restore the pre-cutover active entry or create two live coordination authorities. This recovery path is documented; a full installed rollback was not executed because it would undo the user's requested retirement. The initial failed move did exercise automatic rollback of policy and registry.
