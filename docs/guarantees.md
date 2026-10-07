# Guarantees and boundaries

## Supported configured workflow

`ProjectRuntime.Coverage` in `src/AgentOS.Core/ProjectRuntime.cs` summarizes the contract. Codex CLI 0.160.0 is the sole agent host. `ManagedCodexHost` exposes bounded managed tools; `ManagedSandbox` runs supported PowerShell in an AppContainer with no network capabilities and a private workspace grant. Native shell is disabled. Private Git status/diff/add/commit/log uses `PrivateGit` with sanitized configuration. Publication checks path identities, validates the combined candidate and updates `agent-os/integrated` with expected-old ref semantics. Stale candidates remain private. Validation and local release decisions bind exact commits and evidence.

`GitProjectProfile.Inspect` accepts supported main, linked and bare repositories by canonical common/object identity. Local submodules are pinned and revalidated through `PrivateGit.Modules`. Network paths require `GitNetworkPreconditions` and an actual SMB exclusive-lock proof; failure closes publication. Unsupported topology, changed identities or links fail closed.

`MachineCoordinator` records typed claims, waits, owner identity and recovery in a per-user schema-2 journal. Project schema 3 keeps maps, lineage, interaction history, validation, publication and decisions; migration retains a prior-schema backup. An old schema-1 installed CLI cannot read current state. Compatible App/CLI use the bounded framed `ProjectBroker`, preserving the owning runtime's validation policy. Waiting does not authorize stale work. Owned process trees, loopback previews and the shared evidence ledger have separate lifecycle records.

`TaskMaps` stores dependency, related and followup edges, citations and layout, refuses cycles and starts only selected ready tasks with complete dependencies. Active maps support later partial starts. `InteractiveWork` persists steering, clarification, peer acknowledgement/handoff, waits, followup proposals and required obligations. Proposals do not start automatically. Conflict notices retain candidate and exact cause; a free-form `respond_to_conflict` response is owed before resolution, abandonment or escalation.

`ForegroundCapture` retains HWND/PID/process-creation identity, checks the external foreground window, reads bounded UIA text and can take a screenshot. Review allows redaction and crop; screenshot transmission to `CodexContextMicroagent` is explicit opt-in. The draft requires exactly three suggestions and at most eight generated nodes. Manual nodes can exceed eight. Saving and generating do not start work. `ContextArtifacts` stores immutable hashed bytes; `ContextTurnPayload` passes them as untrusted `additionalContext` only on explicit task start.

## Configured effects

`ExecutionProfileRegistry` requires an available frozen Hyper-V profile for native SDK commands. `HyperVSdkRunner` uses a separate nonadministrator guest worker and trusted collector with an exact operation/nonce/input/output receipt and VM shutdown proof. Missing VM service, credential, receipt or shutdown proof yields unavailable or unknown; there is no host fallback. Windows Sandbox lacks exact shutdown proof and is unavailable for SDK work. Ordinary supported PowerShell remains in AppContainer.

`ExternalEffectRuntime` prepares, approves, executes and reconciles narrowly scoped GitHub, PostgreSQL and deployment effects. Scope freezes destination, commit, evidence, artifact, command and environment hashes. PostgreSQL uses restricted migration grammar and configured TLS/role. Deployment requires a frozen Hyper-V profile plus remote receipt reconciliation and shutdown confirmation. Missing configuration or uncertain outcome cannot become a completed effect. APIs are implemented; live destinations were not configured or mutated here.

`NotificationCenter` records deduplicated local notices with quiet hours; the app provides tray navigation and a close guard. `RuntimeUpdate` implements signed MSIX protocol 1/schema 3 health, drain, readiness, hashes and rollback checks. Production use needs trusted signing and installed prior package metadata. The unsigned portable build is distinct.

## Limits and recovery

The boundary covers this Windows user's managed Agent OS work. It does not coordinate or confine arbitrary unmanaged Windows programs, other accounts, arbitrary native SDK invocations in AppContainer, all Windows platform grants, or unconfigured external services. Native SDK commands have only the explicit Hyper-V path above. The AppContainer negative control and external WinForms fixture establish narrower facts, not a complete OS reference monitor. Remote services and real VM operations remain unverified on this machine.

State and journal corruption fail closed. Project writes use atomic replacement and backup/migration history; known publication intents reconcile against Git after restart without replaying a confirmed effect. Unknown effects remain unknown. Cleanup targets owned paths and processes. A selected update drain rejects new work and waits for readiness; rollback requires unchanged state generation, compatible schema, refs and stopped processes. See [signed update](signed-update.md) and [testing](testing.md).

Historical resource-checkin retirement evidence remains in `docs/retirement.md` and its artifact receipt. It establishes the old release only. Phone, mobile relay and other agent hosts/platforms are deferred or excluded.
Phone work is deferred and explicitly excluded; no mobile app, relay, pairing, phone notifications or phone control is in this release.
