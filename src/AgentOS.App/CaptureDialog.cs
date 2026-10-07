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
    readonly StackPanel suggestions = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    readonly StackPanel editor = new() { Spacing = 6 };
    readonly StackPanel taskStrip = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    readonly Button generate = new() { Content = "Generate suggestions" };
    readonly Button cancelGeneration = new() { Content = "Cancel", IsEnabled = false };
    readonly Button saveButton = new() { Content = "Save draft" };
    readonly Button startButton = new() { Content = "Review and start selected", IsEnabled = false };
    readonly TaskMap draft = new();
    TaskMapCanvas? canvas;
    CancellationTokenSource? generation;
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
    void Changed()=>Changed(true);
    void Changed(bool cancelGeneration) { revision++; if(cancelGeneration)generation?.Cancel(); saved = null; startArmed = false; startButton.Content = "Review and start selected"; startButton.IsEnabled = false; }
    void Say(string text) { message.Text = text; }

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
        body.Children.Add(Note("Inspect this context first. Generating suggestions sends only the checked visible text and screenshot to your configured Codex account. Saving a draft does not start work."));
        var generateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        generateRow.Children.Add(generate); generateRow.Children.Add(cancelGeneration);
        generate.Click += (_, _) => _ = GenerateAsync();
        cancelGeneration.Click += (_, _) => generation?.Cancel();
        body.Children.Add(generateRow);
        for (int i = 0; i < 3; i++)
        {
            var button = new Button { Content = "Suggestion " + (i + 1), IsEnabled = false, MaxWidth = 245 };
            AutomationProperties.SetName(button, "Fill prompt with suggestion " + (i + 1));
            suggestions.Children.Add(button);
        }
        body.Children.Add(suggestions);
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
        editor.Children.Add(taskTitle); editor.Children.Add(prompt); editor.Children.Add(acceptance);
        foreach (var box in new[] { project, mapTitle, taskTitle, prompt, acceptance, visible })
            box.TextChanged += (_, _) => { if(fillingEditor)return;SyncEditing(); Changed(); };
        includeText.Checked += (_, _) => Changed(); includeText.Unchecked += (_, _) => Changed();
        includeImage.Checked += (_, _) => { _=SetPreviewAsync(cropped);Changed(); }; includeImage.Unchecked += (_, _) => { _=SetPreviewAsync(null);Changed(); };
        canvas = new TaskMapCanvas(draft);
        canvas.ProjectPath=()=>project.Text.Trim();
        canvas.ResolveTaskResult = id => ProjectClient.InspectAsync(project.Text.Trim(), id);
        canvas.SelectionChanged += task => { if (task != null) Edit(task); };
        canvas.MapChanged += Changed;
        body.Children.Add(canvas);
        RebuildTaskStrip();
        LoadProject(); cliPath.Text = HostDiscovery.FindCodex() ?? "";
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
            await SetPreviewAsync(includeImage.IsChecked==true?cropped:null); Changed(); Say("Crop applied locally.");
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
        if (busy) return;

        if (capture.Context == null) { Say("Use the manual task editor when capture is unavailable."); return; }
        generation?.Dispose(); generation = CancellationTokenSource.CreateLinkedTokenSource(closing);
        generation.CancelAfter(TimeSpan.FromSeconds(20));
        var token = generation.Token; var at = revision;
        busy = true; generate.IsEnabled = false; cancelGeneration.IsEnabled = true;
        Say("Generating suggestions...");
        try
        {
            var cli = cliPath.Text.Trim();
            if (cli.Length == 0 || !File.Exists(cli)) throw new FileNotFoundException("Choose an installed Codex CLI path. Manual editing remains available.");
            var context = IncludedContext();
            var proposed = await new CodexContextMicroagent(cli).ProposeAsync(context, at.ToString(), token, includeScreenshot:includeImage.IsChecked==true);
            if (at != revision || proposed.IsStale(context.Id, at.ToString())) { Say("Context changed; old suggestions were discarded. Generate again."); return; }
            fillingEditor=true;mapTitle.Text=proposed.Title;draft.Title=proposed.Title;fillingEditor=false;
            for (int i = 0; i < 3; i++)
            {
                var suggestion = proposed.Suggestions[i].Text;
                var suggestionButton = new Button { Content = suggestion, MaxWidth = 245 };
                AutomationProperties.SetName(suggestionButton, "Fill prompt with suggestion " + (i + 1));
                suggestionButton.Click += (_, _) => { prompt.Text = suggestion; Say("Suggestion filled the prompt. Review before saving."); };
                suggestions.Children.RemoveAt(i); suggestions.Children.Insert(i, suggestionButton);
            }
            if (proposed.Nodes.Length > 0)
            {
                draft.Tasks.Clear(); draft.Edges.Clear();
                var ids = proposed.Nodes.ToDictionary(x => x.Id, x => Guid.NewGuid().ToString("N"));
                foreach (var node in proposed.Nodes)
                    draft.Tasks.Add(new MapTask { Id = ids[node.Id], Title = node.Title, Prompt = node.Description, Acceptance = node.AcceptanceCriteria });
                foreach (var node in proposed.Nodes)
                    foreach (var dependency in node.DependsOn) draft.Edges.Add(new MapEdge(ids[dependency], ids[node.Id], MapEdgeKind.Dependency));
                editing = null; Edit(draft.Tasks[0]);
            }
            Changed(cancelGeneration:false); Say("Review the three suggestions and draft tasks. Nothing has started.");
        }
        catch (OperationCanceledException) { Say("Suggestion generation canceled. Manual editing is available."); }
        catch (Exception ex) { Say("Suggestions unavailable: " + ex.Message + " Edit the draft manually or retry."); }
        finally { busy = false; generate.IsEnabled = true; cancelGeneration.IsEnabled = false; }
    }
    static string CaptureSource(ForegroundContext context,string kind,CaptureRect? bounds=null)
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
    public void Dispose() { if (disposed) return; disposed = true; generation?.Cancel(); generation?.Dispose(); }
}







