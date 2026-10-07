using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Text;
using System.Text.Json;

namespace AgentOS.App;

public sealed class CaptureDialog : IDisposable
{
    readonly CaptureResult capture;
    readonly Func<TaskMap, CancellationToken, Task<TaskMap>> save;
    readonly Func<TaskMap, CancellationToken, Task<IReadOnlyList<string>>> start;
    readonly Func<string,ContextArtifactKind,byte[],string,DateTimeOffset,CancellationToken,Task<ContextArtifactRef>> accept;
    readonly CancellationToken closing;
    readonly ContentDialog dialog = new() { Title = "Capture context", CloseButtonText = "Close" };
    readonly TextBox project = new() { Header = "Local Git project", PlaceholderText = @"C:\Projects\my-project" };
    readonly TextBox cliPath = new() { Header = "Codex CLI path", PlaceholderText = "Path to codex.exe or codex.cmd" };
    readonly TextBox mapTitle = new() { Header = "Map title" };
    readonly TextBox taskTitle = new() { Header = "Task title" };
    readonly TextBox prompt = new() { Header = "Prompt", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 75 };
    readonly TextBox acceptance = new() { Header = "Acceptance criteria", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54 };
    readonly TextBox visible = new() { Header = "Visible foreground text", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 75, MaxHeight = 150 };
    readonly CheckBox includeText = new() { Content = "Include visible text", IsChecked = true };
    readonly CheckBox includeImage = new() { Content = "Include screenshot", IsChecked = false };
    readonly TextBox cropX = new() { Header = "Crop X", Width = 72 }, cropY = new() { Header = "Y", Width = 72 }, cropW = new() { Header = "Width", Width = 80 }, cropH = new() { Header = "Height", Width = 80 };
    readonly Image preview = new() { MaxHeight = 170, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    readonly StackPanel suggestions = new() { Orientation = Orientation.Vertical, Spacing = 8 };
    readonly StackPanel editor = new() { Spacing = 6 };
    readonly StackPanel taskStrip = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    readonly Button generate = new() { Content = "Generate suggestions" };
    readonly Button cancelGeneration = new() { Content = "Cancel", IsEnabled = false };
    readonly Button saveButton = new() { Content = "Save draft" };
    readonly Button startButton = new() { Content = "Review and start selected", IsEnabled = false };
    readonly TaskMap draft = new();
    TaskMapCanvas? canvas;
    CancellationTokenSource? generation;
    readonly CaptureSuggestionFlow flow = new();
    readonly ShortcutLaunch?[] launches = new ShortcutLaunch?[3];
    readonly CaptureShortcutRegistry<ShortcutLaunch> launchCache = new();
    readonly Button[] launchButtons = new Button[3];
    readonly TextBlock[] suggestionTexts = new TextBlock[3];
    readonly TextBlock[] rowMessages = new TextBlock[3];
    string[] displayed = new string[3];
    string? displayedProject;
    long displayedContextRevision;
    TaskMap? saved;
    MapTask? editing;
    long revision;
    bool disposed, busy, startArmed, fillingEditor;
    public CaptureDialog(CaptureResult result, Func<TaskMap, CancellationToken, Task<TaskMap>> saveDraft,
        Func<TaskMap, CancellationToken, Task<IReadOnlyList<string>>> startSelected, Func<string,ContextArtifactKind,byte[],string,DateTimeOffset,CancellationToken,Task<ContextArtifactRef>> acceptContext, CancellationToken closingToken)
    { capture = result; save = saveDraft; start = startSelected; accept = acceptContext; closing = closingToken; Build(); }

    static Button Action(string label, Action call)
    { var b = new Button { Content = label }; b.Click += (_, _) => call(); return b; }
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    void Changed() { revision++; flow.EditDraft(); saved = null; startArmed = false; startButton.Content = "Review and start selected"; startButton.IsEnabled = false; }
    void ContextChanged()
    {
        flow.ChangeContext(); generation?.Cancel();
        for (var i = 0; i < 3; i++) { launchButtons[i].IsEnabled = false; rowMessages[i].Text = "Context changed"; }
        _ = flow.DebounceAsync(_ => GenerateAsync(), TimeSpan.FromMilliseconds(600));
    }    void Say(string text) { message.Text = text; }

    void Build()
    {
        var body = new StackPanel { Spacing = 9, MaxWidth = 800 };
        body.Children.Add(project); body.Children.Add(cliPath);
        var context = capture.Context;
        body.Children.Add(Note(context == null ? "Capture unavailable: " + capture.Status + ". You can still create a manual draft."
            : "Foreground: " + context.Window.App + " | " + context.Window.Title));
        body.Children.Add(includeText); body.Children.Add(visible);
        body.Children.Add(includeImage); body.Children.Add(preview);

        var crop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (var box in new[] { cropX, cropY, cropW, cropH }) crop.Children.Add(box);
        crop.Children.Add(Action("Apply crop", () => _ = ApplyCropAsync()));
        body.Children.Add(crop);
        body.Children.Add(Note("Suggestions are submitted automatically to your configured Codex account when this dialog opens and after included context changes. Only checked visible text and an explicitly included applied screenshot crop are sent. Saving a draft does not start work; each Start task button launches its displayed suggestion immediately."));
        var generateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        generateRow.Children.Add(generate); generateRow.Children.Add(cancelGeneration);
        generate.Click += (_, _) => { flow.CancelPending(); _ = GenerateAsync(); };
        cancelGeneration.Click += (_, _) => { flow.CancelPending(); generation?.Cancel(); };
        body.Children.Add(generateRow);
        for (int i = 0; i < 3; i++)
        {
            var index = i;
            var row = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
            var title = Note("Suggestion " + (i + 1) + ": waiting for included context");
            suggestionTexts[i] = title;
            var button = new Button { Content = "Start task", IsEnabled = false };
            AutomationProperties.SetName(button, "Start suggestion " + (i + 1) + " task");
            button.Click += (_, _) => _ = StartSuggestionAsync(index);
            launchButtons[i] = button;
            rowMessages[i] = Note("");
            var actionRow = new Grid { ColumnSpacing = 8 };
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(button, 1);
            actionRow.Children.Add(title); actionRow.Children.Add(button);
            row.Children.Add(actionRow); row.Children.Add(rowMessages[i]);
            suggestions.Children.Add(row);
        }
        body.Children.Add(mapTitle);
        body.Children.Add(taskStrip);
        body.Children.Add(editor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Action("Add task", AddTask));
        actions.Children.Add(Action("Remove task", RemoveTask));
        actions.Children.Add(saveButton); actions.Children.Add(startButton);
        saveButton.Click += (_, _) => _ = SaveAsync();
        startButton.Click += (_, _) => _ = StartAsync();
        body.Children.Add(actions); body.Children.Add(message);
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 660, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        dialog.Closed += (_, _) => Dispose();
        draft.Title = context?.ManualTitle ?? "Manual context map";
        mapTitle.Text = draft.Title;
        visible.Text = context?.VisibleText ?? "";
        includeText.IsEnabled = context != null;
        includeImage.IsEnabled = context?.ScreenshotPng != null;
        if (context?.ScreenshotBounds is { } bounds)
        { cropX.Text = "0"; cropY.Text = "0"; cropW.Text = bounds.Width.ToString(); cropH.Text = bounds.Height.ToString(); }
        _ = SetPreviewAsync(null);
        var first = new MapTask { Title = context?.ManualTitle ?? "New task", Prompt = "Describe the requested work", Acceptance = "Describe the verifiable result" };
        draft.Tasks.Add(first);
        editing = first;
        taskTitle.Text = first.Title; prompt.Text = first.Prompt; acceptance.Text = first.Acceptance;
        editor.Children.Add(taskTitle); editor.Children.Add(prompt); editor.Children.Add(suggestions); editor.Children.Add(acceptance);
        foreach (var box in new[] { project, mapTitle, taskTitle, prompt, acceptance, visible })
            box.TextChanged += (_, _) => { if(fillingEditor)return;SyncEditing(); Changed(); if (box == visible || box == project) ContextChanged(); };
        cliPath.TextChanged += (_, _) => ContextChanged();
        includeText.Checked += (_, _) => { Changed(); ContextChanged(); }; includeText.Unchecked += (_, _) => { Changed(); ContextChanged(); };
        includeImage.Checked += (_, _) => { _=SetPreviewAsync(cropped);Changed(); ContextChanged(); }; includeImage.Unchecked += (_, _) => { _=SetPreviewAsync(null);Changed(); ContextChanged(); };
        canvas = new TaskMapCanvas(draft);
        canvas.ProjectPath=()=>project.Text.Trim();
        canvas.ResolveTaskResult = id => ProjectClient.InspectAsync(project.Text.Trim(), id);
        canvas.SelectionChanged += task => { if (task != null) Edit(task); };
        canvas.MapChanged += Changed;
        body.Children.Add(canvas);
        RebuildTaskStrip();
        LoadProject(); cliPath.Text = HostDiscovery.FindCodex() ?? ""; ContextChanged();
    }
    void LoadProject()
    {
        try
        {
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "desktop.json");
            if (File.Exists(settings))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(settings));
                if (json.RootElement.TryGetProperty("project", out var p)) project.Text = p.GetString() ?? "";
            }
        }

        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    void SyncEditing()
    {
        if (editing == null) return;
        editing.Title = taskTitle.Text.Trim();
        editing.Prompt = prompt.Text.Trim();
        editing.Acceptance = acceptance.Text.Trim();
        draft.Title = mapTitle.Text.Trim();
    }
    void Edit(MapTask task)
    {
        if(editing!=null&&draft.Tasks.Contains(editing))SyncEditing();editing=task;
        fillingEditor=true;taskTitle.Text=task.Title;prompt.Text=task.Prompt;acceptance.Text=task.Acceptance;fillingEditor=false;
        RebuildTaskStrip();
    }
    void RebuildTaskStrip()
    {
        taskStrip.Children.Clear();
        foreach (var task in draft.Tasks)
        {
            var current = task;
            taskStrip.Children.Add(Action(task.Title.Length > 22 ? task.Title[..22] + "..." : task.Title, () => Edit(current)));
            var selected = new CheckBox { Content = "Start", IsChecked = task.Selected };
            selected.Checked += (_, _) => { task.Selected = true; Changed(); canvas?.Refresh(); };
            selected.Unchecked += (_, _) => { task.Selected = false; Changed(); canvas?.Refresh(); };
            taskStrip.Children.Add(selected);
        }
        canvas?.Refresh();
    }
    void AddTask()
    {
        if (draft.Tasks.Count >= 8) { Say("A map supports up to eight tasks."); return; }
        SyncEditing();
        var task = new MapTask { Title = "New task", Prompt = "Describe the work", Acceptance = "Describe the verifiable result" };
        draft.Tasks.Add(task); Changed(); Edit(task);
    }
    void RemoveTask()
    {
        if (editing == null || draft.Tasks.Count == 1) { Say("Keep at least one task."); return; }
        var id = editing.Id; draft.Tasks.Remove(editing);
        draft.Edges.RemoveAll(e => e.FromTaskId == id || e.ToTaskId == id);
        editing = null; Changed(); Edit(draft.Tasks[0]);
    }
    long previewRevision;
    async Task SetPreviewAsync(byte[]? png)
    {
        var at=++previewRevision;
        if (png == null) { preview.Source = null; return; }
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); }
            stream.Seek(0);
            var image = new BitmapImage(); await image.SetSourceAsync(stream); if(at==previewRevision)preview.Source = image;
        }
        catch (Exception ex) { Say("Screenshot preview unavailable: " + ex.Message); }
    }
    byte[]? cropped;
    CaptureRect? cropBounds;
    async Task ApplyCropAsync()
    {
        if (capture.Context?.ScreenshotBounds is not { } bounds) { Say("No screenshot is available."); return; }
        if (!int.TryParse(cropX.Text, out var x) || !int.TryParse(cropY.Text, out var y) ||
            !int.TryParse(cropW.Text, out var w) || !int.TryParse(cropH.Text, out var h) ||
            x < 0 || y < 0 || w < 1 || h < 1 || x + w > bounds.Width || y + h > bounds.Height)
        { Say("Crop must fit within the captured screenshot."); return; }
        try
        {
            var result = capture.Context.CropScreenshot(new CaptureRect(x, y, w, h));
            cropped = result.ScreenshotPng; cropBounds = result.ScreenshotBounds;
            Changed(); ContextChanged(); await SetPreviewAsync(includeImage.IsChecked==true?cropped:null); Say("Crop applied locally.");
        }
        catch (Exception ex) { Say("Crop failed: " + ex.Message); }
    }
    ForegroundContext IncludedContext()
    {
        var source = capture.Context ?? throw new InvalidOperationException("Capture a foreground window before generating suggestions.");
        var text = includeText.IsChecked == true ? visible.Text : "";
        var png = includeImage.IsChecked == true ? cropped ?? throw new InvalidOperationException("Apply a crop before including the screenshot.") : null;
        return new ForegroundContext(source.Id, source.CapturedAt, source.Window, text, png, png == null ? null : cropBounds ?? source.ScreenshotBounds);
    }
    async Task GenerateAsync()
    {
        if (disposed) return;
        generation?.Cancel();
        generation?.Dispose();
        generation = CancellationTokenSource.CreateLinkedTokenSource(closing);
        generation.CancelAfter(TimeSpan.FromSeconds(20));
        var request = generation;
        var token = request.Token;
        var at = flow.ContextRevision;
        var path = cliPath.Text.Trim();
        var projectPath = project.Text.Trim();
        for (var i = 0; i < 3; i++)
        {
            launchButtons[i].IsEnabled = false;
            suggestionTexts[i].Text = "Suggestion " + (i + 1) + ": loading...";
            if (launches[i] == null) rowMessages[i].Text = "";
        }
        generate.IsEnabled = false; cancelGeneration.IsEnabled = true;
        var outcome = "No suggestions available. Refresh to retry.";
        var success = false;
        try
        {
            if (capture.Context == null) { outcome = "No captured context available"; Say(outcome); return; }
            var context = IncludedContext();
            if (string.IsNullOrWhiteSpace(context.VisibleText) && context.ScreenshotPng == null)
            { outcome = "No included context"; Say("Include visible text or an applied screenshot crop to generate suggestions."); return; }
            if (path.Length == 0 || !File.Exists(path)) throw new FileNotFoundException("Choose an installed Codex CLI path.");
            var proposed = await new CodexContextMicroagent(path).ProposeAsync(context, at.ToString(), token, includeScreenshot:context.ScreenshotPng != null);
            if (disposed || token.IsCancellationRequested || at != flow.ContextRevision || proposed.IsStale(context.Id, at.ToString())) return;
            displayedProject = projectPath;
            displayedContextRevision = at;
            for (var i = 0; i < 3; i++)
            {
                var next = proposed.Suggestions[i].Text;
                displayed[i] = next;
                launches[i] = launchCache.Find(at, projectPath, next);
                suggestionTexts[i].Text = "Suggestion " + (i + 1) + ": " + displayed[i];
                rowMessages[i].Text = launches[i]?.Started == true ? "Started" : launches[i]?.InFlight == true ? "Saving and starting task..." : launches[i]?.Error ?? "";
                launchButtons[i].Content = launches[i]?.Started == true ? "Started" : "Start task";
                launchButtons[i].IsEnabled = !string.IsNullOrWhiteSpace(projectPath) && launches[i]?.Started != true && launches[i]?.InFlight != true;
            }
            success = true;
            Say("Suggestions ready. Start a task directly or continue editing the draft.");
        }
        catch (OperationCanceledException) { outcome = "Canceled. Refresh to retry."; if (ReferenceEquals(generation, request)) Say("Suggestion generation canceled. Refresh to retry."); }
        catch (Exception ex) { outcome = "Unavailable: " + ex.Message + " Refresh to retry."; if (ReferenceEquals(generation, request)) Say("Suggestions unavailable: " + ex.Message + " Refresh to retry."); }
        finally
        {
            if (ReferenceEquals(generation, request))
            {
                generation = null;
                if (!disposed) { generate.IsEnabled = true; cancelGeneration.IsEnabled = false; }
                if (!disposed && success)
                    for (var i = 0; i < 3; i++) if (launches[i] is { } item)
                    { rowMessages[i].Text = item.Started ? "Started" : item.InFlight ? "Saving and starting task..." : item.Error;
                      launchButtons[i].Content = item.Started ? "Started" : "Start task"; launchButtons[i].IsEnabled = !item.Started && !item.InFlight; }
                if (!disposed && !success && at == flow.ContextRevision)
                    for (var i = 0; i < 3; i++) suggestionTexts[i].Text = "Suggestion " + (i + 1) + ": " + outcome;
            }
            request.Dispose();
        }
    }    sealed class ShortcutLaunch(TaskMap map, ForegroundContext context, long contextRevision)
    {
        public TaskMap Map { get; } = map;
        public ForegroundContext Context { get; } = context;
        public long ContextRevision { get; } = contextRevision;
        public bool ContextStored { get; set; }
        public bool Started { get; set; }
        public bool InFlight { get; set; }
        public string Error { get; set; } = "";
    }
    async Task StartSuggestionAsync(int index)
    {
        if (disposed || displayedContextRevision != flow.ContextRevision ||
            !string.Equals(displayedProject, project.Text.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(displayedProject) || string.IsNullOrWhiteSpace(displayed[index])) return;
        var launch = launches[index];
        if (launch?.Started == true || launch?.InFlight == true) return;
        if (launch == null)
        {
            ForegroundContext included;
            try { included = IncludedContext(); }
            catch (Exception ex) { rowMessages[index].Text = ex.Message; return; }
            var context = new ForegroundContext(included.Id, included.CapturedAt, included.Window,
                included.VisibleText, included.ScreenshotPng?.ToArray(), included.ScreenshotBounds);
            var map = CaptureShortcutPlan.Create(displayedProject, displayed[index]);
            launch = launchCache.GetOrAdd(flow.ContextRevision, displayedProject!, displayed[index],
                () => new ShortcutLaunch(map, context, flow.ContextRevision));
            launches[index] = launch;
        }
        launch.InFlight = true; launch.Error = "";
        if (CanUpdateRow(index, launch)) { launchButtons[index].IsEnabled = false; rowMessages[index].Text = "Saving and starting task..."; }
        try
        {
            var map = launch.Map;
            var existing = await FindShortcutMapAsync(map);
            if (existing == null)
            {
                if (!launch.ContextStored)
                {
                    map.ContextRefs.Clear();
                    if (!string.IsNullOrWhiteSpace(launch.Context.VisibleText))
                        map.ContextRefs.Add(await accept(map.ProjectPath, ContextArtifactKind.Text,
                            Encoding.UTF8.GetBytes(launch.Context.VisibleText), CaptureSource(launch.Context, "visible text"), launch.Context.CapturedAt, closing));
                    if (launch.Context.ScreenshotPng is { } png)
                        map.ContextRefs.Add(await accept(map.ProjectPath, ContextArtifactKind.Png, png,
                            CaptureSource(launch.Context, "screenshot", launch.Context.ScreenshotBounds), launch.Context.CapturedAt, closing));
                    launch.ContextStored = true;
                }
                try { existing = await save(JsonFormat.Copy(map), closing); }
                catch { existing = await FindShortcutMapAsync(map); if (existing == null) throw; }
            }
            if (existing == null) throw new IOException("The map save was not confirmed.");
            if (CaptureShortcutPlan.IsStarted(existing, map.Tasks[0].Id))
            { MarkStarted(index, launch); return; }
            if (existing.Status != MapStatus.Draft)
                throw new IOException("The map is active, but the task launch is not confirmed. Retry to check its status.");
            try { var started = await start(existing, closing); if (started.Count != 1) throw new IOException("The task launch was not confirmed."); }
            catch
            {
                var checkedMap = await FindShortcutMapAsync(map);
                if (checkedMap != null && CaptureShortcutPlan.IsStarted(checkedMap, map.Tasks[0].Id))
                { MarkStarted(index, launch); return; }
                throw;
            }
            MarkStarted(index, launch);
        }
        catch (Exception ex) { launch.Error = "Start not confirmed: " + ex.Message + " Retry this row."; if (CanUpdateRow(index, launch)) rowMessages[index].Text = launch.Error; }
        finally
        {
            launch.InFlight = false;
            if (!launch.Started && CanUpdateRow(index, launch)) launchButtons[index].IsEnabled = true;
        }
    }
    async Task<TaskMap?> FindShortcutMapAsync(TaskMap snapshot)
    {
        var maps = await ProjectClient.MapsAsync(snapshot.ProjectPath, closing);
        return CaptureShortcutPlan.Match(snapshot, maps);
    }
    bool CanUpdateRow(int index, ShortcutLaunch launch) => !disposed &&
        displayedContextRevision == flow.ContextRevision &&
        CaptureShortcutRegistry<ShortcutLaunch>.RowMatches(launch, launches[index], launch.ContextRevision,
            flow.ContextRevision, launch.Map.ProjectPath, project.Text.Trim(), launch.Map.Tasks[0].Prompt,
            displayed[index], generation != null);    void MarkStarted(int index, ShortcutLaunch launch)
    {
        launch.Started = true;
        if (!CanUpdateRow(index, launch)) return;
        launchButtons[index].Content = "Started";
        launchButtons[index].IsEnabled = false;
        rowMessages[index].Text = "Started";
    }    static string CaptureSource(ForegroundContext context,string kind,CaptureRect? bounds=null)
    {
        var area=bounds is {} b?$" bounds=[{b.X},{b.Y},{b.Width},{b.Height}]":"";
        var raw=$"foreground {kind}:{area} pid={context.Window.ProcessId} born={context.Window.ProcessCreated:O} app={context.Window.App} title={context.Window.Title}";
        return raw.Length<=512?raw:raw[..512];
    }    async Task SaveAsync()
    {
        if (busy) return;
        SyncEditing();
        draft.ProjectPath = project.Text.Trim();
        if (string.IsNullOrWhiteSpace(draft.ProjectPath)) { Say("Choose a local Git project."); return; }
        var at=revision;busy=true;saveButton.IsEnabled=false;
        try
        {
            TaskMapRules.Validate(draft);
            if(includeImage.IsChecked==true&&cropped==null)throw new InvalidOperationException("Apply a crop before including the screenshot.");
            if (capture.Context is { } context)
            {
                draft.ContextRefs.RemoveAll(c=>c.Source.StartsWith("foreground ",StringComparison.Ordinal));
                if(includeText.IsChecked==true&&!string.IsNullOrWhiteSpace(visible.Text))
                    draft.ContextRefs.Add(await accept(draft.ProjectPath,ContextArtifactKind.Text,Encoding.UTF8.GetBytes(visible.Text),CaptureSource(context,"visible text"),context.CapturedAt,closing));
                if(includeImage.IsChecked==true&&cropped is {} png)
                    draft.ContextRefs.Add(await accept(draft.ProjectPath,ContextArtifactKind.Png,png,CaptureSource(context,"screenshot",cropBounds??context.ScreenshotBounds),context.CapturedAt,closing));
            }
            if(at!=revision)throw new InvalidOperationException("Context changed while saving. Review and save again.");
            var result=await save(JsonFormat.Copy(draft),closing);
            if(at!=revision)throw new InvalidOperationException("Draft changed while saving. Review and save again.");
            saved=result;draft.Revision=result.Revision;            visible.IsReadOnly = true; includeText.IsEnabled = false; includeImage.IsEnabled = false;
            foreach (var box in new[] { cropX, cropY, cropW, cropH }) box.IsReadOnly = true;
            startButton.IsEnabled = saved.Tasks.Any(t => t.Selected);
            Say("Draft saved. No task started. Review selected ready tasks before starting.");
        }
        catch (Exception ex) { Say("Save failed: " + ex.Message); }
        finally { busy = false; saveButton.IsEnabled = true; }
    }
    async Task StartAsync()
    {
        if (saved == null || busy) return;
        try

        {
            busy = true; startButton.IsEnabled = false;
            var count = saved.Tasks.Count(t => t.Selected && TaskMapRules.DependenciesComplete(saved, t));
            if (count == 0) { Say("Select a ready task; its dependencies must be complete."); return; }
            if (!startArmed) { startArmed = true; startButton.Content = "Confirm start selected"; Say("Review: " + string.Join(", ", saved.Tasks.Where(t => t.Selected && TaskMapRules.DependenciesComplete(saved, t)).Select(t => t.Title)) + ". Click Confirm to start Codex work in " + saved.ProjectPath + "."); return; }
            var ids = await start(saved, closing);
            Say("Started " + ids.Count + " selected task(s).");
        }
        catch (Exception ex) { Say("Start not confirmed: " + ex.Message); }
        finally { busy = false; startButton.IsEnabled = saved != null; }
    }
    public async Task ShowAsync(XamlRoot root, HotKeyStatus hotkey)
    {
        dialog.XamlRoot = root;
        if (hotkey != HotKeyStatus.Registered)
            Say(hotkey == HotKeyStatus.Conflict ? "Global shortcut is already in use. Capture via the app button or Ctrl+Shift+M." : "Global shortcut unavailable. Capture via the app button or Ctrl+Shift+M.");
        await dialog.ShowAsync();
    }
    public void Dispose() { if (disposed) return; disposed = true; flow.Dispose(); generation?.Cancel(); generation?.Dispose(); }
}




















