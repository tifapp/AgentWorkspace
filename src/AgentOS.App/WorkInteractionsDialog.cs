using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentOS.App;

public sealed class WorkInteractionsDialog
{
    readonly ProjectRuntime runtime;
    readonly XamlRoot root;
    readonly string? workId;
    readonly StackPanel items = new() { Spacing = 10 };
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };

    public WorkInteractionsDialog(ProjectRuntime runtime, XamlRoot root, string? workId = null)
    { this.runtime = runtime; this.root = root; this.workId = workId; }

    public async Task ShowAsync()
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Interactions are saved with this project. Review each request before responding.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(message);
        body.Children.Add(Command("Refresh interactions", () => { Render(); return Task.CompletedTask; }, "RefreshInteractions"));
        body.Children.Add(items);
        Render();
        await new ContentDialog { Title = workId == null ? "Work interactions" : "Task interactions", Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "Close", XamlRoot = root }.ShowAsync();
    }

    Button Command(string title, Func<Task> run, string id)
    {
        var button = new Button { Content = title };
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await run(); message.Text = "Interactions refreshed."; Render(); }
            catch (Exception error) { message.Text = error.Message; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    static TextBox Input(string header) => new() { Header = header, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 120 };
    static string Required(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? throw new ArgumentException("Enter a response first.") : box.Text.Trim();
    void Render()
    {
        items.Children.Clear();
        var interactions = runtime.InspectInteractions(workId).OrderByDescending(x => x.CreatedAt).ToArray();
        if (interactions.Length == 0) items.Children.Add(new TextBlock { Text = "No interactions recorded." });
        var work = runtime.Snapshot.Work.ToDictionary(x => x.Id);
        foreach (var item in interactions)
        {
            var card = new StackPanel { Spacing = 5, Padding = new Thickness(8) };
            work.TryGetValue(item.WorkId, out var owner);
            card.Children.Add(new TextBlock { Text = $"{item.Kind} | {item.Status} | {owner?.ShortTask ?? item.WorkId}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap });
            if (item.TargetWorkId != null) card.Children.Add(new TextBlock { Text = "Target: " + (work.TryGetValue(item.TargetWorkId, out var target) ? target.ShortTask : item.TargetWorkId), TextWrapping = TextWrapping.Wrap });
            if (item.WaitKind != null) card.Children.Add(new TextBlock { Text = $"Wait: {item.WaitKind} | {item.RelatedId ?? item.TargetWorkId ?? item.Scope}", TextWrapping = TextWrapping.Wrap });
            if (item.Required) card.Children.Add(new TextBlock { Text = "Required obligation", TextWrapping = TextWrapping.Wrap });
            if (item.Deadline != null) card.Children.Add(new TextBlock { Text = "Due: " + item.Deadline.Value.LocalDateTime.ToString("g") });
            if (!string.IsNullOrWhiteSpace(item.Response)) card.Children.Add(new TextBlock { Text = "Response: " + item.Response, TextWrapping = TextWrapping.Wrap });
            if (item.Status == InteractionStatus.Pending)
            {
                if (item.Kind == InteractionKind.Clarification)
                {
                    var answer = Input("Clarification response"); card.Children.Add(answer);
                    card.Children.Add(Command("Reply to clarification", async () => { await runtime.ReplyClarificationAsync(item.WorkId, item.Id, Required(answer)); }, "ReplyClarification"));
                }
                else if (item.Kind == InteractionKind.Peer && item.TargetWorkId != null)
                {
                    var answer = Input("Peer response"); card.Children.Add(answer);
                    card.Children.Add(Command("Acknowledge peer", () => { runtime.AcknowledgePeer(item.TargetWorkId, item.Id, Required(answer)); return Task.CompletedTask; }, "AcknowledgePeer"));
                    var targets = new ComboBox { Header = "Hand off to active task" };
                    foreach (var candidate in work.Values.Where(x => x.IsActive && x.Id != item.TargetWorkId)) targets.Items.Add(new ComboBoxItem { Content = candidate.ShortTask, Tag = candidate.Id });
                    card.Children.Add(targets);
                    card.Children.Add(Command("Hand off peer request", () => { if (targets.SelectedItem is not ComboBoxItem choice) throw new ArgumentException("Choose an active task."); runtime.HandoffPeer(item.TargetWorkId, item.Id, (string)choice.Tag); return Task.CompletedTask; }, "HandoffPeer"));
                }
                else if (item.Kind == InteractionKind.Followup)
                {
                    card.Children.Add(new TextBlock { Text = "Accepting starts a new child task. Proposals are never submitted automatically.", TextWrapping = TextWrapping.Wrap });
                    card.Children.Add(Command("Accept and start followup", async () => { await runtime.AcceptFollowup(item.WorkId, item.Id); }, "AcceptFollowup"));
                    card.Children.Add(new TextBlock { Text = "Leave this proposal pending to decline for now; this runtime has no public rejection action.", TextWrapping = TextWrapping.Wrap });
                }
                else if (item.Kind == InteractionKind.Obligation)
                {
                    var resolution = Input("How was this obligation resolved?"); card.Children.Add(resolution);
                    card.Children.Add(Command("Resolve obligation", () => { runtime.ResolveObligation(item.WorkId, item.Id, Required(resolution)); return Task.CompletedTask; }, "ResolveObligation"));
                }
                else if (item.Kind == InteractionKind.Wait)
                {
                    if (item.WaitKind == WaitKind.Resource)
                    { var resolution = Input("Resource resolution"); card.Children.Add(resolution); card.Children.Add(Command("Resolve resource wait", () => { runtime.ResolveWait(item.WorkId, item.Id, Required(resolution)); return Task.CompletedTask; }, "ResolveResourceWait")); }
                    card.Children.Add(Command("Cancel wait", () => { runtime.CancelWait(item.WorkId, item.Id); return Task.CompletedTask; }, "CancelWait"));
                }
            }
            items.Children.Add(new Border { Child = card, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(4) });
        }
    }
}
