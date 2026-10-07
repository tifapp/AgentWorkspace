# Windows isolation findings

### PowerShell needs a workspace-rooted provider drive

- Claim: PowerShell Set-Location through C: failed under the no-capability AppContainer; a Work: drive rooted in the private workspace works without ancestor grants.
- Evidence: artifacts/retirement/appcontainer-traverse3/results.json (failure); artifacts/retirement/appcontainer-onlyworkspace/results.json (pass).
- Checked: 2026-10-07, unversioned experiments; implementation src/AgentOS.Core/ManagedSandbox.cs@68c92e8+dirty.
- Status: supported for this Windows build and PowerShell 5.1. Recheck after provider/runtime changes.
- Why keep: ancestor ACL changes did not solve the issue and caused expensive permission propagation; prototype changes were removed.

### Native Git is not a supported sandbox command

- Claim: Git for Windows 2.30.1 fails opening the null device inside this AppContainer. A native PowerShell invocation initially appeared successful with no Git output; an explicit output check exposed failure.
- Evidence: artifacts/retirement/git-sandbox-process/results.json and git-sandbox.log; artifacts/retirement/codex-native-readonly/codex-managed.jsonl proves the scoped broker's real diff.
- Checked: 2026-10-07 at 68c92e8+dirty.
- Status: supported for tested versions. Recheck after Git or Windows changes.
- Why keep: never accept a command merely because the outer host returned zero.

### Runtime adapter processes need job ownership too

- Claim: Trusted commands start suspended, enter a kill-on-close job, then resume; foreground exit stops descendants.
- Evidence: artifacts/retirement/command-job-test/results.json; src/AgentOS.Core/SuspendedCommand.cs@68c92e8+dirty.
- Checked: 2026-10-07. Status: supported for the exercised descendant path; recheck after launcher changes.

### Whitelisted environment must include Windows path variables

- Claim: Omitting SystemDrive caused Windows cache files to appear beneath the private workspace and fail Git indexing.
- Evidence: artifacts/retirement/desktop-managed/result.json (retained failure); ManagedSandbox environment now supplies SystemDrive, ProgramData, ALLUSERSPROFILE and COMSPEC.
- Checked: 2026-10-07 at 68c92e8+dirty.
- Status: final desktop acceptance passed at 2026-10-07 08:31 UTC; see artifacts/retirement/desktop-final-acceptance/result.json, Core FAD953C3941E958A51156756674402D02C1440F9AAC62A5165246631FC560FE7. Recheck after environment or Windows component changes.
