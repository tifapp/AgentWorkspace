# Windows implementation plan

The starting repository contained only `README.md`. The supplied North Star is the product contract; the macOS rebuild guide is behavioral reference, not evidence that a Windows runtime exists. Codex is the sole supported agent host. This implementation uses .NET 10 LTS and WinUI 3 / Windows App SDK 2.5.1.

1. Build a local runtime with durable state and exclusive ownership per project.
2. Launch Codex with ordinary tools in independent Git clones. Capture streamed host progress and confirmed completion.
3. Prepare immutable candidates, coordinate entry into a managed shared Git branch, reject stale changed paths, and validate the combined tree before atomic publication.
4. Implement an exact-candidate local release decision with durable scope, deduplication, and independent work while pending.
5. Own normal process trees through Windows Job Objects; retain private output and recover incomplete outcomes truthfully.
6. Build the WinUI workflow, run real Codex concurrency and desktop automation, test failure and recovery boundaries, and package a self-contained portable app.

The boundary is deliberately limited: the runtime controls its own integration and local release operations. Same-user arbitrary commands, external services, credentials, and network effects do not cross an enforced reference monitor here. No private edit is described as a shared mutation. Publication parking does not imply that Codex supports parking one native tool call while reasoning continues in that same turn.

## Next objective: replace resource-checkin

The project must outperform and supersede the resource-checkin skill so ordinary Codex work no longer requires it. The implemented publication slice is a foundation for this objective. Follow the [replacement acceptance criteria and milestones](product-objective.md): measure the baseline, establish machine-wide ownership, mediate ordinary effects, implement scheduling/recovery, then demonstrate and migrate a complete replacement. Preserve the current guarantee boundary until broader enforcement is proven.

## Version and API references

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10 is the current LTS.
- [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels): stable SDK selection.
- [Self-contained Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps): portable managed and native dependencies.
- [Codex non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode): JSON events, workspace-write, CLI authentication and task execution. The installed CLI's `exec --help` was also checked.
- [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects): normal child inheritance, lifecycle termination and the WMI limitation.
- [Git update-ref](https://git-scm.com/docs/git-update-ref): expected-old ref publication.
