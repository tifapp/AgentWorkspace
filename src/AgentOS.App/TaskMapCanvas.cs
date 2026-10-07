using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using System.Text.Json;

namespace AgentOS.App;

public sealed class TaskMapCanvas : UserControl
{
    readonly TaskMap map;
    readonly Canvas graph = new() { Width = 900, Height = 560 };
    readonly ScrollViewer scroll = new() { MinHeight = 220, MaxHeight = 350, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly ListView list = new() { MaxHeight = 150 };
    readonly TextBox search = new() { PlaceholderText = "Search tasks" };
    readonly ComboBox from = new(), to = new(), kind = new();
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Dictionary<string, Point> positions = new();
    readonly string layoutFile;
    string? selected, dragging;
    bool panning;
    Point panStart;
    public Func<string, Task<WorkUnit?>>? ResolveTaskResult { get; set; }
    public Func<string>? ProjectPath { get; set; }
    Point dragStart;
    double zoom = 1;
    bool collapsed;
    public TaskMap Map => map;
    public string? SelectedTaskId => selected;
    public event Action<MapTask?>? SelectionChanged;
    public event Action? MapChanged;
    public TaskMapCanvas(TaskMap source)
    {
        map = source;
        layoutFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "layouts", map.Id + ".json");
        LoadLayout();
        var body = new StackPanel { Spacing = 6 };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        tools.Children.Add(search);
        tools.Children.Add(Action("Fit", Fit));
        tools.Children.Add(Action("-", () => Zoom(-0.15)));
        tools.Children.Add(Action("+", () => Zoom(0.15)));
        tools.Children.Add(Action("Layout", AutoLayout));
        tools.Children.Add(Action("Collapse", () => { collapsed = !collapsed; Render(); }));
        body.Children.Add(tools);
        scroll.Content = graph; body.Children.Add(scroll);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (var value in Enum.GetValues<MapEdgeKind>()) kind.Items.Add(value.ToString());
        kind.SelectedIndex = 0;
        links.Children.Add(from); links.Children.Add(kind); links.Children.Add(to);
        links.Children.Add(Action("Connect", Connect));
        links.Children.Add(Action("Remove", Remove));
        body.Children.Add(links);
        AutomationProperties.SetName(list, "Task map list. Select a task and edit its links above.");
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListViewItem item) Select((string)item.Tag, false); };
        body.Children.Add(list); body.Children.Add(status);
        Content = body;
        search.TextChanged += (_, _) => Render();
        graph.PointerPressed += (_, e) => { if (ReferenceEquals(e.OriginalSource, graph)) { panning = true; panStart = e.GetCurrentPoint(graph).Position; } };
        graph.PointerMoved += (_, e) => { if (panning && e.GetCurrentPoint(graph).Properties.IsLeftButtonPressed) { var now = e.GetCurrentPoint(graph).Position; scroll.ChangeView(Math.Max(0, scroll.HorizontalOffset + panStart.X - now.X), Math.Max(0, scroll.VerticalOffset + panStart.Y - now.Y), null); } };
        graph.PointerReleased += (_, _) => panning = false;
        graph.AllowDrop = true;
        graph.DragOver += (_, e) => e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        graph.Drop += async (_, e) =>
        {
            try
            {
                if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    foreach (var item in await e.DataView.GetStorageItemsAsync())
                        if (item is Windows.Storage.StorageFile file)
                        {
                            using var input = await file.OpenStreamForReadAsync();
                            if(input.Length>ContextArtifacts.MaxImageBytes)throw new IOException("File context exceeds 8 MB.");
                            using var bytes=new MemoryStream();var buffer=new byte[81920];int count;while((count=await input.ReadAsync(buffer))>0){if(bytes.Length+count>ContextArtifacts.MaxImageBytes)throw new IOException("File context exceeds 8 MB.");bytes.Write(buffer,0,count);}
                            await AddCitationAsync("file:"+file.Name,bytes.ToArray());
                        }
                else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    { var value = await e.DataView.GetTextAsync(); if (value.StartsWith("task-result:", StringComparison.Ordinal) && ResolveTaskResult != null) { var work = await ResolveTaskResult(value[12..].Trim()); if (work == null) throw new ArgumentException("Task result not found in this project."); await AddTaskResultCitationAsync(work); } else await AddCitationAsync("dropped context", System.Text.Encoding.UTF8.GetBytes(value)); }
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        Render();
    }
    static Button Action(string label, Action call) { var b = new Button { Content = label }; b.Click += (_, _) => call(); return b; }
    public async Task AddCitationAsync(string source,byte[] bytes,CancellationToken cancel=default)
    {
        if(bytes.Length==0)return;var project=ProjectPath?.Invoke();if(string.IsNullOrWhiteSpace(project))throw new InvalidOperationException("Choose an open project before accepting dropped context.");
        bool png=bytes.Length>=8&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10});
        if(bytes.Length>(png?ContextArtifacts.MaxImageBytes:ContextArtifacts.MaxTextBytes))throw new IOException("Dropped context exceeds its size limit.");
        var reference=await ProjectClient.SaveContextAsync(project,png?ContextArtifactKind.Png:ContextArtifactKind.Text,bytes,source,DateTimeOffset.UtcNow,cancel);
        map.ContextRefs.Add(reference);MapChanged?.Invoke();status.Text="Accepted "+source+" for this draft.";
    }
    public Task AddTaskResultCitationAsync(WorkUnit work,CancellationToken cancel=default)=>AddCitationAsync("task result:"+work.Id,System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{work.Id,work.Status,work.CodexReport,work.CandidateCommit,work.IntegratedCommit})),cancel);    Point Position(string id) { if (positions.TryGetValue(id, out var p)) return p; var i = map.Tasks.FindIndex(t => t.Id == id); return new Point(20 + i % 3 * 280, 25 + i / 3 * 150); }
    public void AutoLayout() { for (int i = 0; i < map.Tasks.Count; i++) positions[map.Tasks[i].Id] = new Point(20 + i % 3 * 280, 25 + i / 3 * 150); SaveLayout(); Render(); }
    public void Fit() { zoom = Math.Clamp(Math.Min(Math.Max(scroll.ActualWidth, 350) / 900, Math.Max(scroll.ActualHeight, 220) / 560), .4, 1.4); Zoom(0); scroll.ChangeView(0, 0, null); }
    void Zoom(double change) { zoom = Math.Clamp(zoom + change, .4, 2); graph.RenderTransform = new ScaleTransform { ScaleX = zoom, ScaleY = zoom }; }
    void Select(string id, bool sync = true)
    {
        selected = id;
        if (sync) list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(x => (string)x.Tag == id);
        SelectionChanged?.Invoke(map.Tasks.FirstOrDefault(x => x.Id == id)); Render();
    }
    public void Refresh() => Render();
    internal static string TaskStatusLabel(MapTaskStatus value) => value switch { MapTaskStatus.NeedsResponse => "Needs response", MapTaskStatus.Parked => "Parked conflict", MapTaskStatus.Abandoned => "Abandoned conflict", _ => value.ToString() };
    void Render()
    {
        var fromId = (from.SelectedItem as ComboBoxItem)?.Tag as string;
        var toId = (to.SelectedItem as ComboBoxItem)?.Tag as string;
        from.Items.Clear(); to.Items.Clear(); list.Items.Clear(); graph.Children.Clear();
        foreach (var task in map.Tasks)
        {
            from.Items.Add(new ComboBoxItem { Content = task.Title, Tag = task.Id });
            to.Items.Add(new ComboBoxItem { Content = task.Title, Tag = task.Id });
            list.Items.Add(new ListViewItem { Content = task.Title + " ï¿½ " + TaskStatusLabel(task.Status) + (task.Selected ? " ï¿½ selected" : ""), Tag = task.Id });
        }
        from.SelectedItem = from.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == (fromId ?? selected));
        to.SelectedItem = to.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == toId);
        var visible = map.Tasks.Where(t => t.Title.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var edge in map.Edges)
        {
            if (!visible.Any(t => t.Id == edge.FromTaskId) || !visible.Any(t => t.Id == edge.ToTaskId)) continue;
            var a = Position(edge.FromTaskId); var b = Position(edge.ToTaskId);
            graph.Children.Add(new Line { X1 = a.X + 110, Y1 = a.Y + 42, X2 = b.X + 110, Y2 = b.Y + 42, StrokeThickness = edge.Kind == MapEdgeKind.Dependency ? 3 : 1, Stroke = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue) });
        }
        foreach (var task in visible)
        {
            var p = Position(task.Id);
            var node = new Button { Content = collapsed ? task.Title : task.Title + "\n" + TaskStatusLabel(task.Status) + (task.Selected ? " ï¿½ selected" : ""), Width = 220, Height = collapsed ? 55 : 84, BorderThickness = new Thickness(selected == task.Id ? 3 : 1) };
            AutomationProperties.SetName(node, task.Title + ", " + TaskStatusLabel(task.Status));
            node.Click += (_, _) => Select(task.Id);
            node.PointerPressed += (_, e) => { dragging = task.Id; dragStart = e.GetCurrentPoint(graph).Position; };
            node.PointerMoved += (_, e) =>
            {
                if (dragging != task.Id || !e.GetCurrentPoint(graph).Properties.IsLeftButtonPressed) return;
                var now = e.GetCurrentPoint(graph).Position;
                positions[task.Id] = new Point(Math.Clamp(p.X + now.X - dragStart.X, 0, 660), Math.Clamp(p.Y + now.Y - dragStart.Y, 0, 470));
                Render();
            };
            node.PointerReleased += (_, _) => { dragging = null; SaveLayout(); };
            Canvas.SetLeft(node, p.X); Canvas.SetTop(node, p.Y); graph.Children.Add(node);
        }
    }
    void Connect()
    {
        if (from.SelectedItem is not ComboBoxItem a || to.SelectedItem is not ComboBoxItem b) { status.Text = "Choose two tasks."; return; }
        var edge = new MapEdge((string)a.Tag, (string)b.Tag, Enum.Parse<MapEdgeKind>((string)kind.SelectedItem));
        if (map.Edges.Contains(edge)) { status.Text = "Link already exists."; return; }
        map.Edges.Add(edge);
        try { TaskMapRules.Validate(map); MapChanged?.Invoke(); status.Text = "Link added."; Render(); }
        catch (ArgumentException ex) { map.Edges.Remove(edge); status.Text = ex.Message; }
    }
    void Remove()
    {
        if (from.SelectedItem is not ComboBoxItem a || to.SelectedItem is not ComboBoxItem b) return;
        if (map.Edges.Remove(new MapEdge((string)a.Tag, (string)b.Tag, Enum.Parse<MapEdgeKind>((string)kind.SelectedItem))))
        { MapChanged?.Invoke(); status.Text = "Link removed."; Render(); }
    }
    void LoadLayout()
    {
        try { if (File.Exists(layoutFile)) foreach (var (id, xy) in JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(layoutFile)) ?? [])
            if (xy.Length == 2 && double.IsFinite(xy[0]) && double.IsFinite(xy[1])) positions[id] = new Point(xy[0], xy[1]); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    void SaveLayout()
    {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(layoutFile)!); File.WriteAllText(layoutFile, JsonSerializer.Serialize(positions.ToDictionary(x => x.Key, x => new[] { x.Value.X, x.Value.Y }))); }
        catch (Exception ex) { status.Text = "Could not save layout: " + ex.Message; }
    }
}






