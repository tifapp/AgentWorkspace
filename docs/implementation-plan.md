# Windows implementation plan and current delivery

The repository now contains the Windows/Codex implementation: durable project runtime, managed Codex tools, private Git publication, maps and interactions, native desktop capture/review, conditional Git and Hyper-V profiles, scoped external adapters, notifications and signed-update protocol. The source boundary is summarized in [guarantees](guarantees.md). Phone work is deferred; other hosts and platforms are excluded.

The immediate delivery path is:

1. Preserve the completed root-native 71/71 receipt for exact archived `0d8a7e9bc9ead47b21b3f2495483ee7c0f134613`: `artifacts/integration-final-regression/results.json` and `artifacts/integration-native-acceptance-evidence.json`. The results source field inherited parent checkout 9d; use the receipt for source identity.
2. Publish side by side with `scripts/build.ps1 -Test -Publish` to `artifacts/release/agent-os-integrated-win-x64` and its matching ZIP. Use its compatible CLI. Preserve the installed retirement bundle and historical receipts.
3. Validate rendered UI when a capture mechanism produces nonblank screenshots. Current UIA controls/bounds checks do not prove rendering.
4. Provision a suitable Hyper-V host/profile and production signing material, then separately exercise real SDK VM receipts, live external destinations and signed MSIX update/rollback before claiming those deployments verified.

The current native App build 73d completed with zero warnings/errors. Owned capture and synthetic authenticated drafting proofs passed their stated fixtures. Injected SDK/adapter tests are narrower than real VM or remote operation evidence. `ValidateCompactUi.ps1` and source validation are structural. No installer, retirement migration, global instruction change or external deployment is part of this delivery.

Implementation anchors: `ProjectRuntime.cs` and `Storage.cs` for state/publication; `TaskMaps.cs` and `InteractiveWork.cs` for graph and inbox; `ForegroundCapture.cs`, `ContextMicroagent.cs` and `ContextArtifacts.cs` for context; `GitProjectProfiles.cs` and `PrivateGit.Modules.cs` for repositories; `ExecutionProfiles.cs` and `HyperVExecution.cs` for SDK/deployment isolation; `ExternalEffectRuntime.cs` for scoped effects; `RuntimeUpdate.cs` and packaging scripts for update protocol.

Phone work is deferred and explicitly excluded; no mobile app, relay, pairing, phone notifications or phone control is in this release.
