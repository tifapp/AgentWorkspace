using AgentOS.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Text.Json;
using Windows.Storage.Pickers;

namespace AgentOS.App;

public sealed class MainWindow : Window
{
    private static readonly SolidColorBrush Ink = Brush(0x1D, 0x2D, 0x28), Muted = Brush(0x55, 0x66, 0x5E), Accent = Brush(0x0F, 0x6E, 0x56), Line = Brush(0xD9, 0xE1, 0xDC);
    private readonly Grid _root = new();
    private readonly StackPanel _navigation = new() { Spacing = 8, Padding = new Thickness(20, 28, 20, 20) };
    private readonly Grid _body = new() { Padding = new Thickness(28, 22, 28, 24), RowSpacing = 18 };
    private readonly TextBlock _projectTitle = Text("Open a project", 26, true);
    private readonly TextBlock _projectSubtitle = Text("A local workspace for autonomous Codex work", 13);
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBox _projectPath = new() { Header = "Local Git project", PlaceholderText = @"C:\Projects\my-project", MinWidth = 280 };
    private readonly TextBox _validation = new() { Header = "Validation command (PowerShell syntax)", PlaceholderText = "dotnet test; exit $LASTEXITCODE", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
    private readonly TextBox _prompt = new() { Header = "What should Codex do?", PlaceholderText = "Describe a change, its intended behavior, and how to check it…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 86, MaxHeight = 160 };
    private readonly CheckBox _auto = new() { Content = "Validate and integrate automatically", IsChecked = true };
    private readonly ListView _workList = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _details = new() { Spacing = 14, Padding = new Thickness(22, 8, 12, 20) };
    private readonly StackPanel _decisions = new() { Spacing = 16 };
    private readonly StackPanel _evidence = new() { Spacing = 14 };
    private readonly StackPanel _prerequisites = new() { Spacing = 10 };
    private readonly TextBlock _navProject = Text("No project open", 12);
    private readonly TextBlock _counts = Text("", 12);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly string _settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "desktop.json");
    private ProjectRuntime? _runtime;
    private ProjectState? _snapshot;
    private string _activePage = "Setup";
    private string? _selected;
    private long _generation = -1;
    private bool _closing, _rebuilding, _walkthrough;
    private string? _dataRoot;

    public MainWindow()
    {
        Title = "agent os · Codex workspace";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        _root.RequestedTheme = ElementTheme.Light;
        _root.Background = Brush(0xF5, 0xF7, 0xF4);
        _root.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        _root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var side = new Border { Background = Brush(0xEA, 0xEF, 0xEA), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = _navigation };
        _root.Children.Add(side);
        var mark = Text("a /", 30, true); mark.Foreground = Accent;
        _navigation.Children.Add(mark);
        _navigation.Children.Add(Text("agent os", 22, true));
        _navigation.Children.Add(Text("CODEX WORKSPACE", 10, true));
        _navProject.Margin = new Thickness(0, 24, 0, 12);
        _navigation.Children.Add(_navProject);
        foreach (var name in new[] { "Workspace", "Decisions", "Evidence", "Setup" })
        {
            var nav = Button(name, () => { ShowPage(name); return Task.CompletedTask; }, "Nav" + name);
            nav.HorizontalAlignment = HorizontalAlignment.Stretch;
            nav.HorizontalContentAlignment = HorizontalAlignment.Left;
            nav.Padding = new Thickness(12, 10, 12, 10);
            _navigation.Children.Add(nav);
        }
        var footer = Text("Ordinary tools.\nCoordinated results.", 12); footer.Margin = new Thickness(0, 36, 0, 0);
        _navigation.Children.Add(footer);
        _navigation.Children.Add(_counts);
        Grid.SetColumn(_body, 1); _root.Children.Add(_body);
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 5 }; heading.Children.Add(_projectTitle); heading.Children.Add(_projectSubtitle);
        _body.Children.Add(heading);
        Grid.SetRow(_notice, 1); _body.Children.Add(_notice);
        Grid.SetRow(_page, 2); _body.Children.Add(_page);
        Content = _root;
        AutomationProperties.SetAutomationId(_projectPath, "ProjectPath");
        AutomationProperties.SetAutomationId(_validation, "ValidationCommand");
        AutomationProperties.SetAutomationId(_prompt, "TaskPrompt");
        AutomationProperties.SetAutomationId(_workList, "WorkList");
        _workList.SelectionChanged += (_, _) => { if (!_rebuilding && _workList.SelectedItem is ListViewItem item) { _selected = (string)item.Tag; DrawDetails(); } };
        _root.SizeChanged += (_, e) => { var compact = e.NewSize.Width < 1000; _root.ColumnDefinitions[0].Width = new(compact ? 165 : 210); _body.Padding = new Thickness(compact ? 16 : 28, 22, compact ? 16 : 28, 24); };
        AppWindow.Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; _closing = true; _timer.Stop();
            _notice.IsOpen = true; _notice.Title = "Stopping owned processes and saving state…";
            if (_runtime != null) await _runtime.DisposeAsync();
            Close();
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        ShowPage("Setup");
        _root.Loaded += async (_, _) => await Guard(Initialize);
    }

    private async Task Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i + 1 < args.Length; i++)
        { if (args[i] == "--project") _projectPath.Text = args[++i]; else if (args[i] == "--data-root") _dataRoot = args[++i]; }
        if (string.IsNullOrWhiteSpace(_projectPath.Text) && File.Exists(_settings))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_settings));
            if (doc.RootElement.TryGetProperty("project", out var p)) _projectPath.Text = p.GetString() ?? "";
            if (_dataRoot == null && doc.RootElement.TryGetProperty("dataRoot", out var d)) _dataRoot = d.GetString();
        }
        await CheckPrerequisites();
        if (Directory.Exists(_projectPath.Text)) await OpenProject();
    }
    private async Task CheckPrerequisites()
    {
        _prerequisites.Children.Clear(); _prerequisites.Children.Add(Text("Checking this machine…", 13));
        var checks = await HostDiscovery.CheckAsync(); _prerequisites.Children.Clear();
        foreach (var check in checks)
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(Text((check.Ready ? "✓  " : "!  ") + check.Name + (check.Ready ? " · ready" : " · action needed"), 14, true));
            panel.Children.Add(Text(check.Detail, 12)); _prerequisites.Children.Add(panel);
        }
    }

    private async Task OpenProject()
    {
        if (_runtime?.Snapshot.Work.Any(x => x.IsActive) == true) throw new InvalidOperationException("Cancel or finish active work before switching projects.");
        if (_runtime != null) { await _runtime.DisposeAsync(); _runtime = null; }
        _runtime = await ProjectRuntime.OpenAsync(_projectPath.Text, _dataRoot);
        _snapshot = _runtime.Snapshot; _validation.Text = _snapshot.ValidationCommand;
        if (string.IsNullOrWhiteSpace(_validation.Text) && File.Exists(Path.Combine(_snapshot.ProjectPath, "Validate.ps1"))) _validation.Text = PracticeProject.ValidateCommand;
        _projectTitle.Text = _snapshot.ProjectName;
        _projectSubtitle.Text = _snapshot.ProjectPath + "  ·  Shared result: agent-os/integrated";
        _navProject.Text = _snapshot.ProjectName;
        Directory.CreateDirectory(Path.GetDirectoryName(_settings)!);
        await File.WriteAllTextAsync(_settings, JsonSerializer.Serialize(new { project = _snapshot.ProjectPath, dataRoot = _dataRoot == null ? null : Path.GetFullPath(_dataRoot) }));
        _generation = -1; Refresh();
        if (!string.IsNullOrWhiteSpace(_snapshot.ValidationCommand)) ShowPage("Workspace");
        else Notice("Project opened", "Set its validation command below. Tasks start from committed Git state, not uncommitted editor changes.");
    }
    private void ShowPage(string page)
    {
        foreach (var element in new UIElement[] { _projectPath, _validation, _prompt, _auto, _workList, _details, _prerequisites, _decisions, _evidence }) Detach(element);
        _activePage = page;
        if (_runtime == null && page != "Setup") { _activePage = "Setup"; Notice("Open a project first", "Choose a local Git project or create a practice project to get started."); }
        foreach (var button in _navigation.Children.OfType<Button>())
        { var active = (string)button.Content == _activePage; button.Background = active ? Accent : new SolidColorBrush(Colors.Transparent); button.Foreground = active ? new SolidColorBrush(Colors.White) : Ink; }
        _page.Content = _activePage switch { "Workspace" => WorkspacePage(), "Decisions" => Scroll(_decisions), "Evidence" => Scroll(_evidence), _ => SetupPage() };
        Refresh(true);
    }

    private UIElement SetupPage()
    {
        var stack = new StackPanel { Spacing = 20, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Text("Project setup", 22, true));
        stack.Children.Add(Text("Give Codex a project and a way to validate its work. Each task starts in a private clone of the shared result.", 14));
        stack.Children.Add(_projectPath);
        stack.Children.Add(Row(Button("Browse…", Browse, "BrowseProject"), Button("Open project", OpenProject, "OpenProject", true), Button("Create practice project", CreatePractice, "CreatePractice")));
        stack.Children.Add(_validation);
        stack.Children.Add(Text("This command runs on the combined candidate before integration. It must exit with a nonzero code on failure. Validation has a 15 minute limit. Run only trusted project commands.", 12));
        stack.Children.Add(Row(Button("Save setup", () => { RequireRuntime().Configure(_validation.Text, HostDiscovery.FindCodex()); Notice("Setup saved", "Codex tasks will use this validation command."); ShowPage("Workspace"); return Task.CompletedTask; }, "SaveSetup", true), Button("Check prerequisites", CheckPrerequisites, "CheckPrerequisites")));
        stack.Children.Add(Card("Mediation for this configuration", Text(ProjectRuntime.Coverage, 13)));
        stack.Children.Add(Card("Prerequisites", _prerequisites));
        stack.Children.Add(Text("The app bundles .NET and the Windows App SDK. Git and an authenticated Codex CLI are required to start work. Model and account settings come from your Codex configuration. Host failures never fall back to an unconfined launch.", 12));
        return Scroll(stack);
    }
    private async Task Browse()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder != null) _projectPath.Text = folder.Path;
    }
    private async Task CreatePractice()
    {
        _projectPath.Text = await PracticeProject.CreateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "practice"));
        await OpenProject(); _validation.Text = PracticeProject.ValidateCommand;
        RequireRuntime().Configure(_validation.Text); ShowPage("Workspace");
        Notice("Practice project ready", "Use Run concurrency check to exercise real Codex tasks, a stale candidate, independent work, and a scoped decision.");
    }

    private UIElement WorkspacePage()
    {
        var grid = new Grid { RowSpacing = 16 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var compose = new StackPanel { Spacing = 10 };
        compose.Children.Add(_prompt);
        compose.Children.Add(Row(Button("Start Codex task", StartTask, "StartTask", true), _auto, Button("Run concurrency check", Walkthrough, "RunWalkthrough")));
        grid.Children.Add(compose);
        var split = new Grid { ColumnSpacing = 10 };
        split.ColumnDefinitions.Add(new() { Width = new GridLength(0.38, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new() { Width = new GridLength(0.62, GridUnitType.Star) });
        var listPanel = new Grid { RowSpacing = 10 };
        listPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); listPanel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        listPanel.Children.Add(Text("Task map", 16, true)); Grid.SetRow(_workList, 1); listPanel.Children.Add(_workList);
        split.Children.Add(listPanel);
        var detailBorder = new Border { Background = new SolidColorBrush(Colors.White), BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = Scroll(_details) };
        Grid.SetColumn(detailBorder, 1); split.Children.Add(detailBorder); Grid.SetRow(split, 1); grid.Children.Add(split);
        return grid;
    }
    private async Task StartTask()
    {
        var runtime = RequireRuntime();
        _selected = await runtime.StartAsync(_prompt.Text, _auto.IsChecked == true);
        _prompt.Text = ""; Refresh(true);
    }
    private async Task Walkthrough()
    {
        if (_walkthrough) throw new InvalidOperationException("The concurrency check is already running.");
        if (!File.Exists(Path.Combine(RequireRuntime().Snapshot.ProjectPath, "settings.json")) || !RequireRuntime().Snapshot.ProjectName.StartsWith("coordination-"))
            throw new InvalidOperationException("Create a practice project in Setup to run the concurrency check.");
        _walkthrough = true;
        try { await PracticeProject.RunWalkthroughAsync(RequireRuntime(), text => DispatcherQueue.TryEnqueue(() => Notice("Concurrency check", text))); Notice("Concurrency check complete", "Review the integrated work, stale attempt, Codex revision, and pending local release decision."); }
        finally { _walkthrough = false; }
    }

    private void Refresh(bool force = false)
    {
        if (_runtime == null) return;
        var state = _runtime.Snapshot;
        if (!force && state.Generation == _generation) return;
        _generation = state.Generation; _snapshot = state;
        _counts.Text = $"{state.Work.Count(x => x.IsActive)} active · {state.Work.Count(x => x.Status == WorkStatus.Completed)} integrated\n{state.Decisions.Count(x => x.Status == DecisionStatus.Pending)} decisions waiting";
        _rebuilding = true;
        _workList.Items.Clear();
        foreach (var (work, depth) in TaskMap(state.Work))
        {
            var panel = new StackPanel { Spacing = 7, Padding = new Thickness(6, 10, 6, 10) };
            var status = Text(work.StatusLabel.ToUpperInvariant(), 10, true); status.Foreground = StatusBrush(work.Status);
            panel.Children.Add(status); panel.Children.Add(Text(work.ShortTask, 14, true));
            panel.Children.Add(Text(work.IsActive || work.Status is WorkStatus.Stale or WorkStatus.Failed or WorkStatus.Unknown ? work.Detail :
                work.Status == WorkStatus.Completed ? "Validated · available in the shared result" : work.Status == WorkStatus.Private ? "Ready for validation · files remain private" : "Stopped · private output retained", 12));
            var pending = state.Decisions.Count(x => x.WorkId == work.Id && x.Status == DecisionStatus.Pending);
            if (pending > 0) { var decision = Text("Release decision pending", 12, true); decision.Foreground = StatusBrush(WorkStatus.Stale); panel.Children.Add(decision); }
            panel.Children.Add(Text(work.CreatedAt.ToLocalTime().ToString("HH:mm") + " · Codex" + (work.ParentId == null ? "" : " · revision"), 11));
            var content = new Border { Child = panel, Margin = new Thickness(Math.Min(depth, 3) * 12, 0, 0, 0), BorderBrush = Line, BorderThickness = new Thickness(depth > 0 ? 2 : 0, 0, 0, 0) };
            var item = new ListViewItem { Tag = work.Id, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, (depth > 0 ? "Revision · " : "") + work.StatusLabel + ": " + work.ShortTask);
            _workList.Items.Add(item); if (work.Id == _selected) _workList.SelectedItem = item;
        }
        if (_selected == null && _workList.Items.Count > 0) { _workList.SelectedIndex = 0; _selected = (string)((ListViewItem)_workList.SelectedItem).Tag; }
        _rebuilding = false;
        DrawDetails(); DrawDecisions(); DrawEvidence();
    }

    private void DrawDetails()
    {
        _details.Children.Clear();
        var work = _snapshot?.Work.FirstOrDefault(x => x.Id == _selected);
        if (work == null)
        {
            _details.Children.Add(Text("Room to work", 23, true));
            _details.Children.Add(Text("Start a Codex task above. You can launch another while it works. Their files and Git indexes remain private until validated integration.", 14));
            _details.Children.Add(Text("You will see waiting, failed, stale, and unknown outcomes here, along with the evidence behind completed work.", 13)); return;
        }
        var label = Text(work.StatusLabel, 13, true); label.Foreground = StatusBrush(work.Status); _details.Children.Add(label);
        _details.Children.Add(Text(work.ShortTask, 20, true));
        _details.Children.Add(Text(work.Detail, 14));
        if (work.Status == WorkStatus.Waiting)
            _details.Children.Add(Card("Waiting for shared publication", Text("This candidate resumes when the current publication finishes. Changed files are checked again against the latest shared version, then the combined result is tested. Other Codex work can continue. Cancel removes this candidate from the queue.", 13)));
        if (work.ParentId != null || _snapshot!.Work.Any(x => x.ParentId == work.Id))
        {
            var related = new StackPanel { Spacing = 8 };
            if (work.ParentId != null)
            {
                var parent = _snapshot!.Work.FirstOrDefault(x => x.Id == work.ParentId);
                if (parent != null) related.Children.Add(Button("Original task · " + parent.StatusLabel, () => SelectWork(parent.Id), "OriginalTask"));
            }
            foreach (var child in _snapshot!.Work.Where(x => x.ParentId == work.Id))
                related.Children.Add(Button("Revision · " + child.StatusLabel, () => SelectWork(child.Id), "RevisionTask"));
            related.Children.Add(Text("Each attempt keeps its own outcome and evidence. A revision does not change the earlier record.", 12));
            _details.Children.Add(Card("Task thread", related));
        }
        if (_snapshot!.Decisions.Any(x => x.WorkId == work.Id && x.Status == DecisionStatus.Pending))
            _details.Children.Add(Button("Review pending release decision", () => { ShowPage("Decisions"); return Task.CompletedTask; }, "TaskDecision"));
        var actions = new FlowPanel();
        if (work.IsActive) actions.Children.Add(Button("Cancel task", () => { RequireRuntime().Cancel(work.Id); return Task.CompletedTask; }, "CancelTask"));
        if (work.Status == WorkStatus.Private) actions.Children.Add(Button("Integrate candidate", () => RequireRuntime().IntegrateAsync(work.Id), "IntegrateCandidate", true));
        if (!work.IsActive) actions.Children.Add(Button("Revise with Codex", async () => { _selected = await RequireRuntime().ReviseAsync(work.Id); Refresh(true); }, "ReviseTask"));
        if (work.Status == WorkStatus.Completed) actions.Children.Add(Button("Prepare release decision", async () => { await RequireRuntime().RequestReleaseAsync(work.Id); ShowPage("Decisions"); }, "PrepareRelease", true));
        _details.Children.Add(actions);
        var files = new FlowPanel();
        if (Directory.Exists(work.Workspace)) files.Children.Add(Button("Open private files", () => OpenPath(work.Workspace), "OpenPrivate"));
        files.Children.Add(Button("Open transcript", () => OpenPath(RequireRuntime().TranscriptPath(work.Id)), "OpenTranscript"));
        if (File.Exists(RequireRuntime().DiagnosticsPath(work.Id))) files.Children.Add(Button("Runtime diagnostics", () => OpenPath(RequireRuntime().DiagnosticsPath(work.Id)), "OpenDiagnostics"));
        if (!work.IsActive && !work.WorkspaceRemoved) files.Children.Add(Button("Clean private files", () => RequireRuntime().CleanupAsync(work.Id), "CleanupTask"));
        _details.Children.Add(files);
        if (!string.IsNullOrWhiteSpace(work.CodexReport))
        {
            var report = new Expander { Header = "Codex report", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Code(work.CodexReport, 320) };
            AutomationProperties.SetAutomationId(report, "CodexReport"); _details.Children.Add(report);
        }
        _details.Children.Add(new Expander { Header = "Full task", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Code(work.Task, 280) });
        if (work.IntegratedCommit != null) _details.Children.Add(Card("Shared result", Text("Commit " + work.IntegratedCommit + "\nBranch agent-os/integrated\nInspect with: git show " + work.IntegratedCommit, 12)));
        if (work.ChangedPaths.Count > 0) _details.Children.Add(Card("Changed files", Text(string.Join("\n", work.ChangedPaths), 13)));
        if (!string.IsNullOrWhiteSpace(work.Diff)) _details.Children.Add(new Expander { Header = "Review candidate diff", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Code(work.Diff, 320) });
        foreach (var evidence in work.Evidence) _details.Children.Add(EvidenceCard(evidence));
        _details.Children.Add(Text("Recent activity", 15, true));
        foreach (var entry in _snapshot!.Events.Where(x => x.WorkId == work.Id).TakeLast(12).Reverse())
            _details.Children.Add(Text(entry.At.ToLocalTime().ToString("HH:mm:ss") + "  " + entry.Message, 12));
    }

    private Task SelectWork(string id) { _selected = id; Refresh(true); _workList.ScrollIntoView(_workList.SelectedItem); return Task.CompletedTask; }
    private static IEnumerable<(WorkUnit Work, int Depth)> TaskMap(List<WorkUnit> work)
    {
        var seen = new HashSet<string>();
        IEnumerable<(WorkUnit, int)> Branch(WorkUnit item, int depth)
        {
            if (!seen.Add(item.Id)) yield break;
            yield return (item, depth);
            foreach (var child in work.Where(x => x.ParentId == item.Id))
                foreach (var row in Branch(child, depth + 1)) yield return row;
        }
        foreach (var root in work.Where(x => x.ParentId == null || !work.Any(p => p.Id == x.ParentId)).Reverse())
            foreach (var row in Branch(root, 0)) yield return row;
        foreach (var orphan in work.Where(x => !seen.Contains(x.Id)))
            foreach (var row in Branch(orphan, 0)) yield return row;
    }

    private void DrawDecisions()
    {
        _decisions.Children.Clear(); _decisions.Children.Add(Text("Decisions", 22, true));
        _decisions.Children.Add(Text("Only a specific authority or unresolved judgment belongs here. Waiting for another task does not require permission.", 14));
        if (_snapshot?.Decisions.Count == 0) _decisions.Children.Add(Card("Nothing needs a decision", Text("Authorized work can continue. A completed task can prepare a reviewed local release candidate.", 13)));
        foreach (var decision in _snapshot?.Decisions.AsEnumerable().Reverse() ?? [])
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(Text(decision.Status.ToString(), 12, true));
            content.Children.Add(Text(decision.Explanation, 14));
            content.Children.Add(Text("Authority: " + decision.Scope, 13));
            content.Children.Add(Code("Candidate: " + decision.Candidate + "\nDestination: " + decision.Destination, 90));
            if (decision.Note != null) content.Children.Add(Text(decision.Note, 13));
            content.Children.Add(Button("Inspect candidate and evidence", () => { _selected = decision.WorkId; ShowPage("Workspace"); return Task.CompletedTask; }, "InspectDecision"));
            if (decision.Status == DecisionStatus.Pending)
                content.Children.Add(Row(Button("Approve this local release", () => RequireRuntime().DecideAsync(decision.Id, true), "ApproveDecision", true), Button("Decline release", () => RequireRuntime().DecideAsync(decision.Id, false), "RejectDecision")));
            _decisions.Children.Add(Card("Designate a reviewed release", content));
        }
    }
    private void DrawEvidence()
    {
        _evidence.Children.Clear(); _evidence.Children.Add(Text("Results & evidence", 22, true));
        _evidence.Children.Add(Text("Passing results apply to the recorded candidate tree, validation command, and environment. A newer candidate needs its own validation.", 14));
        _evidence.Children.Add(Row(Button("Open evidence folder", () => OpenPath(RequireRuntime().DataDirectory), "OpenEvidenceFolder"), Button("Open project folder", () => OpenPath(RequireRuntime().Snapshot.ProjectPath), "OpenProjectFolder")));
        foreach (var work in _snapshot?.Work.AsEnumerable().Reverse() ?? [])
            foreach (var evidence in work.Evidence) _evidence.Children.Add(Card(work.ShortTask, EvidenceCard(evidence)));
        _evidence.Children.Add(Text("Effect history", 17, true));
        foreach (var entry in _snapshot?.Events.Where(x => x.Kind != "Codex").TakeLast(80).Reverse() ?? [])
            _evidence.Children.Add(Text(entry.At.ToLocalTime().ToString("HH:mm:ss") + " · " + entry.Kind + " · " + entry.Message, 12));
    }
    private UIElement EvidenceCard(ValidationEvidence e)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text(e.TestedAt.ToLocalTime().ToString("g") + " · exit " + e.ExitCode + " · " + (e.SourceUnchanged ? "source unchanged" : "source changed"), 12));
        content.Children.Add(Code("Commit " + e.Commit + "\nTree " + e.Tree + "\nBased on " + e.AgainstCommit + "\n" + e.Command, 125));
        content.Children.Add(Text(e.Environment, 12));
        content.Children.Add(Button("Open validation log", () => OpenPath(e.LogPath), "OpenValidationLog"));
        return Card(e.Passed ? "Validation passed" : "Validation failed", content);
    }
    private ProjectRuntime RequireRuntime() => _runtime ?? throw new InvalidOperationException("Open a project in Setup first.");
    private static Task OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("This artifact is not available yet.", path);
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
        {
            var editor = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            editor.ArgumentList.Add(path); Process.Start(editor);
        }
        return Task.CompletedTask;
    }
    private void Notice(string title, string message, bool error = false)
    { _notice.Title = title; _notice.Message = message; _notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; _notice.IsOpen = true; }
    private async Task Guard(Func<Task> action)
    { try { await action(); Refresh(true); } catch (Exception e) { Notice("Could not complete this action", e.Message, true); } }
    private Button Button(string text, Func<Task> action, string automationId, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(13, 9, 13, 9) };
        if (primary) { button.Background = Accent; button.Foreground = new SolidColorBrush(Colors.White); }
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Guard(action); } finally { button.IsEnabled = true; } };
        return button;
    }
    private static Panel Row(params UIElement[] elements)
    { var panel = new FlowPanel(); foreach (var e in elements) panel.Children.Add(e); return panel; }
    private static TextBlock Text(string text, double size = 14, bool bold = false) => new()
    { Text = text, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = bold ? Ink : Muted, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static TextBox Code(string text, double maxHeight) => new() { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, MaxHeight = maxHeight, Text = text.ReplaceLineEndings("\r\n") };
    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static Border Card(string title, UIElement content)
    { var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(Text(title, 15, true)); panel.Children.Add(content); return new() { Child = panel, Background = new SolidColorBrush(Colors.White), BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(18) }; }
    private static SolidColorBrush StatusBrush(WorkStatus status) => status switch { WorkStatus.Completed => Accent, WorkStatus.Failed => Brush(0xA3, 0x28, 0x28), WorkStatus.Stale or WorkStatus.Unknown => Brush(0x87, 0x55, 0x0D), _ => Muted };
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private static void Detach(UIElement element)
    {
        switch (VisualTreeHelper.GetParent(element))
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Border border: border.Child = null; break;
            case ScrollViewer scroll: scroll.Content = null; break;
            case ContentControl content: content.Content = null; break;
        }
    }
}
