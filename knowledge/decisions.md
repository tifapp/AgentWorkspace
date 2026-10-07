# Decisions

## Windows/Codex operating boundary

Codex CLI 0.160.0 is the supported agent host on Windows x64. Managed PowerShell runs in AppContainer; private Git uses sanitized broker tools. Canonical main/linked/bare Git profiles and pinned local modules are supported; network/SMB needs an actual lock proof. Native SDK work requires an available frozen Hyper-V profile, separate nonadministrator worker and exact VM shutdown receipt. No host fallback is authorized. External GitHub/PostgreSQL/deployment effects require configured destination, scoped review and reconciliation. Unmanaged programs and other accounts are outside mediation.

## Historical retirement

The resource-checkin retirement archive and receipt document a prior operational cutover. Original host-protected files may remain discoverable; the old release receipts identify old binaries only. Do not rerun retirement or installation, delete old artifacts, or edit global Codex instructions as part of the integrated portable delivery. Preserve the old bundle; build the compatible current CLI side by side.

## Current delivery and evidence

Use project schema 3 and machine-journal schema 2 with compatible App/CLI broker. Build output is `artifacts/release/agent-os-integrated-win-x64` and matching ZIP. Exact archived 0d8a7e9 passed 71/71 root-native integration checks; receipt pins archive and DLL hashes. Preview UIA controls/bounds and capture/drafting fixtures prove their stated checks only; blank screenshots do not prove rendering. SDK/adapter fixtures do not prove actual VM or live external effects. Phone remains deferred; other hosts/platforms are excluded.

Phone work is deferred and explicitly excluded; no mobile app, relay, pairing, phone notifications or phone control is in this release.
