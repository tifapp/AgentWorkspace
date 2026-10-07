# Conflict continuity verification

The executable scenarios are registered in `tests/AgentOS.Tests/ConflictContinuityChecks.cs` with individually filterable `Conflict ` names. They use `PracticeProject`, real Git refs and trees, and `ProjectRuntime` with deterministic script hosts.

## Claim / Evidence / Checked

**Claim.** All 23 conflict continuity scenarios passed across two native pinned SDK runs against the identical Core source tree `4526e35ac58568b0594c4322a16281a4e2d0167e`. This is not a single 23/23 suite run or a full project suite run.

**Evidence.** The pinned SDK build succeeded for implementation `0e6dc52efe31ea3bbd0e27384147d1035f9ea81d` and assertion-only correction `c830ff3d3b0b9ece41ae25c8d56cf1a84731379f`. The initial expanded run passed 22/23; its only failure checked stale `Snapshot` before `response`. After correcting that assertion, the targeted rerun passed 1/1 and verified retention of the original deferred Git ref and full recovery. The receipt at `C:/Users/seani/ctv-report/conflict-results.json` preserves each run revision and DLL hashes, including the initial fixture failure; each run also has a `source-manifest.json`.

**Checked.** Five related regression runs passed: Wait cycles; Feature integration map attempts/journal; Peer delivery exact interrupt wait escalation persistence; Exact patch failure; and External ref validation CAS race. The continuation scenario calls the production `ManagedCodexHost.RunContinuationLoopAsync` driver with scripted turn transport and checks bounded nonresponse, thread identity after restart, canceled and failed turns, and later successful resolution. The production peer delivery pump was tested with scripted failed, successful, repeated, and target-ends-during-callback transports. These checks used real Git and runtime behavior, but did not establish live Codex app-server protocol acceptance.

The validation-time race uses a real third task candidate and moves the Git shared ref from the validation command to reproduce a late external publication. The third task does not execute a concurrent runtime publication call because the runtime publication gate serializes that call. Peer delivery checks distinguish a queued durable message from an acknowledged peer request. A terminal target keeps an undelivered message queued after restart; `MarkPeerDelivered` rejects that terminal target.
