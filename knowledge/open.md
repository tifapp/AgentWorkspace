# Open verification and prerequisites

- Exact archived `0d8a7e9` source passed 71/71 root-native checks. Preserve `artifacts/integration-final-regression/results.json` with `artifacts/integration-native-acceptance-evidence.json`; the results source field inherited parent checkout 9d, so the receipt is the source identity.
- Obtain nonblank rendered screenshots or another direct visual check. Native App build 73d had zero warnings/errors and preview UIA controls/bounds passed wide/narrow, but current-session and retained 3fbb screenshots are blank.
- Configure a real Hyper-V host with `vmcompute`, frozen base VHD/profile and separate guest/worker credentials to verify SDK/deployment VM receipt, nonadministrator collector and exact shutdown. Current tests use injected fixtures and this machine lacks `vmcompute`.
- Exercise a real SMB-backed Git source with an exclusive-lock proof before claiming live network-filesystem behavior. Current conditional SMB path used fixtures only.
- Configure isolated live GitHub, PostgreSQL and deployment destinations and credentials for real effect/reconciliation proof. Current tests use fixtures; no live remote mutation occurred.
- Supply trusted Code Signing certificate, SDK packaging tools, prior signed package/metadata and an eligible installation to verify production MSIX install, drain and rollback. Unsigned portable and diagnostic layouts are separate.
- Arbitrary unmanaged Windows programs, other accounts, phone and non-Codex hosts/platforms are outside the approved boundary. Historical retirement and no-op benchmark receipts do not establish current whole-task performance or broad host protection.

Structural `scripts/ValidateCompactUi.ps1` and source validation are useful gates, but they do not close these items.
