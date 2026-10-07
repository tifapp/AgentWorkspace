# Decisions

## D1 — Managed Codex tools and scoped Git, 2026-10-07

Use Codex app-server 0.160.0 with dynamic managed tools. Native shell is disabled and the native working directory is read-only. PowerShell mutations and validation use an AppContainer; private Git uses a sanitized broker adapter. The authenticated reasoning host remains outside the command sandbox. Chosen by the implementation agent within the user's Codex-only delegation.

Evidence: src/AgentOS.Core/ManagedCodexHost.cs@68c92e8+dirty; artifacts/retirement/codex-native-readonly/results.json (real model source edit and Git diff). Recheck after Codex protocol, model configuration, tool exposure or sandbox changes.

## D2 — Retire only after evidence and legacy drain, 2026-10-07

Archive the installed skill reversibly, replace global instructions and leave an unsupported legacy registry schema. Refuse cutover with other active legacy tasks. Do not imply unsupported external operations became protected. User explicitly requested retirement on 2026-10-07.

Evidence: user request; scripts/retire-resource-checkin.ps1@68c92e8+dirty; artifacts/retirement/retirement-receipt.json and cutover-verification.json. Status: completed operational retirement at 2026-10-07 08:33 UTC. Host permissions denied moving the installed directory; rollback worked, then a preserved archive copy, replacement instructions and unsupported registry schema disabled legacy execution. Original files remain discoverable. Recheck after migration or policy changes.

## Approved platform boundary (2026-10-07)

This program is Windows/Codex only. Phone work is deferred and explicitly excluded: no mobile app, relay, pairing, phone notifications, or phone control is implemented or approved for this release. Future phone work needs a separate decision and acceptance evidence.
