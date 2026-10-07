# Integration progress

## Supplied verification, 2026-10-07

The primary agent exported integrated source `3fbb628` as a Git archive at `artifacts/combined-check.zip` / `artifacts/combined-check`. On that archive, native `dotnet build AgentOS.App -c Release` succeeded with 0 warnings and 0 errors, and native `dotnet run` for `tests/AgentOS.Tests -c Release` completed 46/46. Evidence: `artifacts/combined-tests/results.json` and `artifacts/combined-tests/source-manifest.json`. The result JSON Git source field inherited parent checkout `9d9b8be`; it does not identify the tested source. Use archive `3fbb628` plus the content manifest, including core SHA-256 `C1662C18B5754EC3AD88F54A4AAA0C7F154423BB9F3F5CA37BEA51E33B7E9455`, to identify it. These were native SDK commands outside AppContainer mediation.

An earlier native suite completed 41/41 at `artifacts/vfix`; it does not validate newer code. A live synthetic-window `CodexContextMicroagent` fixture at `artifacts/context-live` reported `Passed: true`, 3 suggestions and 3 nodes within 20 seconds using an account. It used no real foreground capture or screenshot, and its source at `artifacts/approved-source` predates `3fbb628`.

## Integration incident and recovery

At 2026-10-07 10:20:30 UTC, a new regression binary migrated the user machine journal from schema 1 to 2. The old installed runtime rejected schema 2; all 8 queued feature tasks failed publication/commands. Private candidate commits were retained. The old runtime eventually stopped. The primary agent compiled a compatible self-contained CLI from `3fbb628` at `artifacts/integration-runtime/cli` and launched a normal run; managed validation was saved. Do not downgrade or delete the journal or live claims. Test fixture coordinators must use a root isolated from the user `DefaultRoot`; a live journal with incompatible owners must not be migrated automatically. Receipts from old binaries apply only to those binaries. Managed coordinator isolation fix task `9a6daa2207124ad69ad8716a1a04a28a` remains underway.

## Boundary and next check

Approved scope is Windows/Codex. Phone work is explicitly deferred; no other hosts or platforms are approved. The Windows 10 Education 19045 machine lacks `vmcompute` and `WindowsSandbox.exe`, so real VM confinement has not been verified. Signed installation and GitHub, Postgres, or deployment effects have not been verified or provisioned. Source features remain underway, not finished. Recheck after final integration and protocol/profile updates. The supplied runs establish the stated build, tests and fixture outcomes only; they do not show new code passing the earlier tests or establish managed mediation for native SDK compilation.
