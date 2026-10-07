using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Text.Json;
using Windows.Storage.Pickers;
using Windows.System;

namespace AgentOS.App;
public sealed class MainWindow : Window
{
    readonly Grid root = new() { RowSpacing = 8, Padding = new Thickness(16,12,16,12) };
    readonly Grid surface = new() { ColumnSpacing = 12 };
    readonly StackPanel tree = new() { Spacing = 8 };
    readonly ScrollViewer map = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly ScrollViewer detail = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel detailBody = new() { Spacing = 10 };
    readonly TextBlock projectName = Label("Open a project",18,true), summary = Label("",12);
    readonly TextBlock detailHeading = Label("",18,true), detailStatus = Label("",13);
    readonly Button decisions = new() { Content = "Decisions" };
    readonly InfoBar notice = new() { IsOpen = false, IsClosable = true };
    readonly TextBox prompt = new() { Header = "New task", PlaceholderText = "Describe the change and how to check it", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 136 };
    readonly TextBox projectPath = new() { Header = "Local Git project", PlaceholderText = @"C:\Projects\my-project" };
    readonly TextBox validation = new() { Header = "Validation command (PowerShell syntax)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
    readonly CheckBox auto = new() { Content = "Validate and integrate automatically", IsChecked = true };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    readonly Dictionary<string, TaskRow> rows = new();
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","desktop.json");
    ProjectRuntime? runtime; ProjectState? snapshot; string? dataRoot, selected;
    long generation = -1; bool closing, detailsOpen, wide, walkthrough;

    public MainWindow()
    {
        Title = "Agent OS"; AppWindow.Resize(new Windows.Graphics.SizeInt32(1040,760));
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header = new Grid{ColumnSpacing=8};
        header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var identity = new StackPanel{Spacing=1}; identity.Children.Add(projectName); identity.Children.Add(summary); header.Children.Add(identity);
        var controls = new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        controls.Children.Add(Action("Project",ShowSettings,"ProjectChooser"));
        controls.Children.Add(Action("Settings",ShowSettings,"Settings"));
        controls.Children.Add(Action("Maps",ShowMaps,"TaskMaps"));
        decisions.Click += (_,_) => ShowDecisionMenu();
        AutomationProperties.SetAutomationId(decisions,"PendingDecisions"); controls.Children.Add(decisions);
        Grid.SetColumn(controls,1); header.Children.Add(controls); root.Children.Add(header);
        Grid.SetRow(notice,1); root.Children.Add(notice);
        map.Content=tree; AutomationProperties.SetAutomationId(map,"WorkList");
        surface.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        surface.ColumnDefinitions.Add(new(){Width=new GridLength(0)});
        surface.Children.Add(map); detail.Content=detailBody; Grid.SetColumn(detail,1); surface.Children.Add(detail);
        Grid.SetRow(surface,2); root.Children.Add(surface);
        var composer=new StackPanel{Spacing=5}; composer.Children.Add(prompt);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        actions.Children.Add(Action("Send",SendTask,"StartTask",true));
        var options=new Button{Content="Options",Flyout=new Flyout{Content=auto}};
        AutomationProperties.SetName(options,"Task options: automatic integration"); ToolTipService.SetToolTip(options,"Choose automatic validation and integration"); actions.Children.Add(options);
        composer.Children.Add(actions); Grid.SetRow(composer,3); root.Children.Add(composer);
        Content=root;
        AutomationProperties.SetAutomationId(prompt,"TaskPrompt");
        ToolTipService.SetToolTip(prompt,"Ctrl+N focuses this field. Ctrl+Enter sends this task.");
        AutomationProperties.SetAutomationId(projectPath,"ProjectPath");
        AutomationProperties.SetAutomationId(validation,"ValidationCommand");
        root.SizeChanged+=(_,e)=>{wide=e.NewSize.Width>=1000; ArrangeDetails();};
        Shortcut(VirtualKey.N,VirtualKeyModifiers.Control,()=>prompt.Focus(FocusState.Programmatic));
        Shortcut(VirtualKey.Enter,VirtualKeyModifiers.Control,()=>{if(prompt.FocusState!=FocusState.Unfocused)_=Guard(SendTask);});
        Shortcut(VirtualKey.Escape,VirtualKeyModifiers.None,()=>{detailsOpen=false; ArrangeDetails(); notice.IsOpen=false;});
        AppWindow.Closing+=async (_,e)=>{if(closing)return; e.Cancel=true; closing=true; timer.Stop(); if(runtime!=null)await runtime.DisposeAsync(); Close();};
        timer.Tick+=(_,_)=>Refresh(); timer.Start();
        root.Loaded+=async (_,_)=>await Guard(Initialize);
    }
    void Shortcut(VirtualKey key,VirtualKeyModifiers modifiers,Action action)
    {var a=new KeyboardAccelerator{Key=key,Modifiers=modifiers}; a.Invoked+=(_,e)=>{action();e.Handled=true;}; root.KeyboardAccelerators.Add(a);}
    async Task Initialize()
    {
        var args=Environment.GetCommandLineArgs();
        for(int i=1;i+1<args.Length;i++){if(args[i]=="--project")projectPath.Text=args[++i];else if(args[i]=="--data-root")dataRoot=args[++i];}
        if(string.IsNullOrWhiteSpace(projectPath.Text)&&File.Exists(settings))
        {using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(settings));
         if(doc.RootElement.TryGetProperty("project",out var p))projectPath.Text=p.GetString()??"";
         if(dataRoot==null&&doc.RootElement.TryGetProperty("dataRoot",out var d))dataRoot=d.GetString();}
        if(Directory.Exists(projectPath.Text))await OpenProject();else await ShowSettings();
    }
    async Task OpenProject()
    {
        if(runtime?.Snapshot.Work.Any(x=>x.IsActive)==true)throw new InvalidOperationException("Finish or cancel active tasks before switching projects.");
        if(runtime!=null){await runtime.DisposeAsync();runtime=null;}
        runtime=await ProjectRuntime.OpenAsync(projectPath.Text,dataRoot); snapshot=runtime.Snapshot;
        validation.Text=snapshot.ValidationCommand;
        if(string.IsNullOrWhiteSpace(validation.Text)&&File.Exists(Path.Combine(snapshot.ProjectPath,"Validate.ps1")))validation.Text=PracticeProject.ValidateCommand;
        projectName.Text=snapshot.ProjectName;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings,JsonSerializer.Serialize(new{project=snapshot.ProjectPath,dataRoot=dataRoot==null?null:Path.GetFullPath(dataRoot)}));
        rows.Clear();tree.Children.Clear();selected=null;detailsOpen=false;generation=-1;Refresh();
        if(string.IsNullOrWhiteSpace(snapshot.ValidationCommand))Notice("Project opened","Set and save its validation command in Settings.");
    }

    async Task ShowSettings()
    {
        var body=new StackPanel{Spacing=12};
        body.Children.Add(projectPath);
        body.Children.Add(Row(Action("Browse…",Browse,"BrowseProject"),Action("Open project",OpenProject,"OpenProject")));
        body.Children.Add(validation);
        body.Children.Add(Action("Save setup",()=>{RequireRuntime().Configure(validation.Text,HostDiscovery.FindCodex());Notice("Setup saved","Validation command saved.");return Task.CompletedTask;},"SaveSetup"));
        body.Children.Add(Label("A local Git project, Git, and an authenticated Codex CLI are required. Tasks start from committed state.",12));
        var checks=new StackPanel{Spacing=5};
        body.Children.Add(new Expander{Header="Prerequisites",Content=checks});
        body.Children.Add(Action("Check prerequisites",async()=>{checks.Children.Clear();foreach(var c in await HostDiscovery.CheckAsync())checks.Children.Add(Label((c.Ready?"Ready: ":"Action needed: ")+c.Name+" — "+c.Detail,12));},"CheckPrerequisites"));
        var advanced=new StackPanel{Spacing=8};
        advanced.Children.Add(Label(ProjectRuntime.Coverage,12));
        advanced.Children.Add(Action("Create practice project",CreatePractice,"CreatePractice"));
        advanced.Children.Add(Action("Run concurrency check",Walkthrough,"RunWalkthrough"));
        advanced.Children.Add(Action("Open evidence folder",()=>OpenPath(RequireRuntime().DataDirectory),"OpenEvidenceFolder"));
        advanced.Children.Add(Action("Open project folder",()=>OpenPath(RequireRuntime().Snapshot.ProjectPath),"OpenProjectFolder"));
        body.Children.Add(new Expander{Header="Advanced: coverage, practice and concurrency",Content=advanced});
        var dialog=new ContentDialog{Title="Project settings",Content=new ScrollViewer{Content=body,MaxHeight=540,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},CloseButtonText="Close",XamlRoot=root.XamlRoot};
        await dialog.ShowAsync();
    }
    async Task ShowMaps()
    {
        var r=RequireRuntime();
        var body=new StackPanel{Spacing=10};
        body.Children.Add(Label("Draft task maps",18,true));
        body.Children.Add(Label("Edit the JSON, save a draft, then select ready tasks to start. Saving never starts Codex.",12));
        var editor=new TextBox{Header="Editable task map",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=220,MaxHeight=350};
        AutomationProperties.SetAutomationId(editor,"TaskMapEditor"); body.Children.Add(editor);
        var picker=new ComboBox{Header="Saved maps",MinWidth=260};
        foreach(var saved in r.Snapshot.Maps)picker.Items.Add(new ComboBoxItem{Content=saved.Title+" · "+saved.Status,Tag=saved.Id});
        picker.SelectionChanged+=(_,_)=>{if(picker.SelectedItem is ComboBoxItem item)editor.Text=JsonSerializer.Serialize(r.Snapshot.Maps.Single(m=>m.Id==(string)item.Tag),JsonFormat.Options);};
        body.Children.Add(picker);
        body.Children.Add(Row(Action("New draft from prompt",()=>
        {
            var value=prompt.Text.Trim();if(value.Length==0)throw new ArgumentException("Enter a prompt in the task editor first.");
            var title=value.Length>70?value[..70]:value;
            var draft=new AgentOS.Core.TaskMap{Title=title,Tasks=[new MapTask{Title=title,Prompt=value,Acceptance="Describe the verifiable result before starting."}]};
            editor.Text=JsonSerializer.Serialize(draft,JsonFormat.Options);return Task.CompletedTask;
        },"NewMapDraft"),Action("Save reviewed draft",()=>
        {
            var draft=JsonSerializer.Deserialize<AgentOS.Core.TaskMap>(editor.Text,JsonFormat.Options)??throw new ArgumentException("The draft is empty.");
            var existing=r.Snapshot.Maps.SingleOrDefault(m=>m.Id==draft.Id);
            var id=r.SaveDraftMap(draft,existing==null?null:draft.Revision);
            editor.Text=JsonSerializer.Serialize(r.Snapshot.Maps.Single(m=>m.Id==id),JsonFormat.Options);
            Notice("Draft saved","Select ready tasks and start them explicitly.");return Task.CompletedTask;
        },"SaveMapDraft")));
        body.Children.Add(Action("Start selected ready tasks",async()=>
        {
            var draft=JsonSerializer.Deserialize<AgentOS.Core.TaskMap>(editor.Text,JsonFormat.Options)??throw new ArgumentException("Choose a saved map first.");
            var saved=r.Snapshot.Maps.SingleOrDefault(m=>m.Id==draft.Id)??throw new InvalidOperationException("Save the draft before starting.");
            if(saved.Revision!=draft.Revision || JsonSerializer.Serialize(saved,JsonFormat.Options)!=JsonSerializer.Serialize(draft,JsonFormat.Options))throw new InvalidOperationException("Save or reload edited map details before starting.");
            var launched=await r.StartSelectedMapTasksAsync(saved.Id);Notice("Tasks started",string.Join(", ",launched));
        },"StartMapTasks"));
        var dialog=new ContentDialog{Title="Task maps",Content=new ScrollViewer{Content=body,MaxHeight=560},CloseButtonText="Close",XamlRoot=root.XamlRoot};
        await dialog.ShowAsync();
    }
    async Task Browse()
    {var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var folder=await picker.PickSingleFolderAsync();if(folder!=null)projectPath.Text=folder.Path;}
    async Task CreatePractice()
    {projectPath.Text=await PracticeProject.CreateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","practice"));await OpenProject();validation.Text=PracticeProject.ValidateCommand;RequireRuntime().Configure(validation.Text);Notice("Practice project ready","Run the concurrency check from Advanced settings.");}
    async Task Walkthrough()
    {
        if(walkthrough)throw new InvalidOperationException("The concurrency check is already running.");
        if(!File.Exists(Path.Combine(RequireRuntime().Snapshot.ProjectPath,"settings.json"))||!RequireRuntime().Snapshot.ProjectName.StartsWith("coordination-"))throw new InvalidOperationException("Create a practice project first.");
        walkthrough=true;
        try{await PracticeProject.RunWalkthroughAsync(RequireRuntime(),message=>DispatcherQueue.TryEnqueue(()=>Notice("Concurrency check",message)));}
        finally{walkthrough=false;}
    }
    async Task SendTask()
    {var value=prompt.Text;var id=await Commands.Start(value,auto.IsChecked==true);prompt.Text="";selected=id;Refresh();}
    async Task SendFollowUp(string parentId,string value)
    {var id=await Commands.Start(value,auto.IsChecked==true,parentId);rows[parentId].ClearReply();selected=id;Refresh();}
    void Refresh()
    {
        if(runtime==null)return;
        var state=runtime.Snapshot;if(state.Generation==generation)return;
        generation=state.Generation;snapshot=state;
        var pending=state.Decisions.Count(x=>x.Status==DecisionStatus.Pending);
        summary.Text=$"{state.Work.Count(x=>x.IsActive)} active · {state.Work.Count(x=>x.Status==WorkStatus.Completed)} completed";
        AutomationProperties.SetLiveSetting(summary,AutomationLiveSetting.Polite);
        decisions.Content=pending==0?"Decisions":$"Decisions ({pending})";decisions.IsEnabled=pending>0;
        var scrollOffset=map.VerticalOffset;
        var live=state.Work.Select(x=>x.Id).ToHashSet();
        foreach(var id in rows.Keys.Where(x=>!live.Contains(x)).ToArray())
        {var row=rows[id];if(row.Root.Parent is Panel p)p.Children.Remove(row.Root);rows.Remove(id);}
        foreach(var work in state.Work)
        {
            if(!rows.TryGetValue(work.Id,out var row))
            {row=new TaskRow(work.Id,id=>OpenDetails(id),(id,value)=>Guard(()=>SendFollowUp(id,value)),id=>Guard(()=>{Commands.Stop(id);return Task.CompletedTask;}),id=>Guard(()=>ShowWorkDecision(id)),()=>RowMenu(work.Id));rows.Add(work.Id,row);}
            row.Patch(work,state.Decisions.Any(x=>x.WorkId==work.Id&&x.Status==DecisionStatus.Pending));
        }
        Reconcile(tree,state.Work.Where(x=>x.ParentId==null||!rows.ContainsKey(x.ParentId)).Reverse().Select(x=>x.Id).ToArray());
        foreach(var work in state.Work){Reconcile(rows[work.Id].Children,state.Work.Where(x=>x.ParentId==work.Id).Select(x=>x.Id).ToArray());rows[work.Id].UpdateChildren();}
        DispatcherQueue.TryEnqueue(()=>{if(Math.Abs(map.VerticalOffset-scrollOffset)>1)map.ChangeView(null,scrollOffset,null,true);});
        if(detailsOpen){var shown=state.Work.FirstOrDefault(x=>x.Id==selected);if(shown!=null){detailHeading.Text=shown.StatusLabel+" · "+shown.ShortTask;detailStatus.Text=shown.Detail;}}
    }
    void Reconcile(StackPanel parent,string[] ids)
    {
        for(int i=0;i<ids.Length;i++)
        {var target=rows[ids[i]].Root;
         if(target.Parent is Panel old&&old!=parent)old.Children.Remove(target);
         var current=parent.Children.IndexOf(target);if(current==i)continue;
         if(current>=0)parent.Children.RemoveAt(current);
         parent.Children.Insert(i,target);}
    }
    void OpenDetails(string id){selected=id;detailsOpen=true;DrawDetails();ArrangeDetails();}
    void ArrangeDetails()
    {
        surface.ColumnDefinitions[0].Width=new GridLength(detailsOpen&&wide?0.55:1,GridUnitType.Star);
        surface.ColumnDefinitions[1].Width=new GridLength(detailsOpen?(wide?0.45:1):0,GridUnitType.Star);
        map.Visibility=detailsOpen&&!wide?Visibility.Collapsed:Visibility.Visible;
        detail.Visibility=detailsOpen?Visibility.Visible:Visibility.Collapsed;
    }

    void DrawDetails()
    {
        detailBody.Children.Clear();
        var work=snapshot?.Work.FirstOrDefault(x=>x.Id==selected);if(work==null)return;
        detailBody.Children.Add(Row(Action("Back",()=>{detailsOpen=false;ArrangeDetails();return Task.CompletedTask;},"BackToTasks"),Action("Refresh details",()=>{DrawDetails();return Task.CompletedTask;},"RefreshDetails")));
        detailHeading.Text=work.StatusLabel+" · "+work.ShortTask;detailBody.Children.Add(detailHeading);
        detailStatus.Text=work.Detail;detailBody.Children.Add(detailStatus);
        if(work.ParentId!=null&&snapshot!.Work.FirstOrDefault(x=>x.Id==work.ParentId) is { } original)
            detailBody.Children.Add(Action("Parent task",()=>{OpenDetails(original.Id);return Task.CompletedTask;},"OriginalTask"));
        foreach(var child in snapshot!.Work.Where(x=>x.ParentId==work.Id))
            detailBody.Children.Add(Action("Child task · "+child.StatusLabel,()=>{OpenDetails(child.Id);return Task.CompletedTask;},"RevisionTask"));
        detailBody.Children.Add(Card("Full prompt",ReadOnly(work.Task,200)));
        if(!string.IsNullOrWhiteSpace(work.CodexReport))detailBody.Children.Add(Card("Full report",ReadOnly(work.CodexReport,300)));
        if(work.ChangedPaths.Count>0)detailBody.Children.Add(Card("Changed files",ReadOnly(string.Join(Environment.NewLine,work.ChangedPaths),160)));
        if(!string.IsNullOrWhiteSpace(work.Diff))detailBody.Children.Add(Card("Candidate diff",ReadOnly(work.Diff,300)));
        if(work.IntegratedCommit!=null)detailBody.Children.Add(Label("Integrated commit: "+work.IntegratedCommit,12));
        foreach(var evidence in work.Evidence)
        {var panel=new StackPanel{Spacing=5};
         panel.Children.Add(Label($"{evidence.TestedAt.LocalDateTime:g} · exit {evidence.ExitCode} · {(evidence.Passed?"passed":"failed")}",12));
         panel.Children.Add(ReadOnly($"Commit {evidence.Commit}\nTree {evidence.Tree}\nBased on {evidence.AgainstCommit}\n{evidence.Command}\n{evidence.Environment}",170));
         panel.Children.Add(Action("Open validation log",()=>OpenPath(evidence.LogPath),"OpenValidationLog"));
         detailBody.Children.Add(Card("Validation evidence",panel));}
        detailBody.Children.Add(Label("Recent activity",15,true));
        foreach(var entry in snapshot!.Events.Where(x=>x.WorkId==work.Id).TakeLast(20).Reverse())
            detailBody.Children.Add(Label(entry.At.ToLocalTime().ToString("g")+" · "+entry.Message,12));
    }
    MenuFlyout RowMenu(string id)
    {
        var menu=new MenuFlyout();var work=snapshot?.Work.FirstOrDefault(x=>x.Id==id);if(work==null)return menu;
        void Add(string title,Func<Task> action,string automationId)
        {var item=new MenuFlyoutItem{Text=title};AutomationProperties.SetAutomationId(item,automationId);item.Click+=async(_,_)=>await Guard(action);menu.Items.Add(item);}
        if(work.IsActive)Add("Stop task",()=>{Commands.Stop(id);return Task.CompletedTask;},"CancelTask");
        if(work.Status==WorkStatus.Private)Add("Integrate candidate",()=>Commands.Integrate(id),"IntegrateCandidate");
        if(!work.IsActive)Add("Revise with Codex",async()=>{selected=await Commands.Revise(id);Refresh();},"ReviseTask");
        if(work.Status==WorkStatus.Completed)Add("Prepare release decision",async()=>{await Commands.PrepareRelease(id);Refresh();},"PrepareRelease");
        if(Directory.Exists(work.Workspace))Add("Open private files",()=>OpenPath(work.Workspace),"OpenPrivate");
        Add("Open transcript",()=>OpenPath(Commands.Transcript(id)),"OpenTranscript");
        if(File.Exists(Commands.Diagnostics(id)))Add("Runtime diagnostics",()=>OpenPath(Commands.Diagnostics(id)),"OpenDiagnostics");
        if(!work.IsActive&&!work.WorkspaceRemoved)Add("Clean private files",()=>Commands.Cleanup(id),"CleanupTask");
        return menu;
    }
    void ShowDecisionMenu()
    {
        var menu=new MenuFlyout();
        foreach(var choice in snapshot?.Decisions.Where(x=>x.Status==DecisionStatus.Pending).Reverse()??[])
        {
            var item=new MenuFlyoutItem{Text=(snapshot?.Work.FirstOrDefault(x=>x.Id==choice.WorkId)?.ShortTask??"Task")+" · "+choice.Candidate[..Math.Min(8,choice.Candidate.Length)]};
            item.Click+=async(_,_)=>await Guard(()=>ShowDecision(choice.Id));menu.Items.Add(item);
        }
        menu.ShowAt(decisions);
    }
    Task ShowWorkDecision(string workId)
    {var decision=snapshot?.Decisions.LastOrDefault(x=>x.WorkId==workId&&x.Status==DecisionStatus.Pending);return decision==null?Task.CompletedTask:ShowDecision(decision.Id);}
    async Task ShowDecision(string decisionId)
    {
        var decision=snapshot?.Decisions.FirstOrDefault(x=>x.Id==decisionId&&x.Status==DecisionStatus.Pending);if(decision==null)return;
        var work=snapshot!.Work.FirstOrDefault(x=>x.Id==decision.WorkId);
        var content=new StackPanel{Spacing=8};
        content.Children.Add(Label(work?.ShortTask??"Task",16,true));
        content.Children.Add(Label(decision.Explanation,13));
        content.Children.Add(Card("Exact candidate and destination",ReadOnly(decision.Candidate+"\n"+decision.Destination,120)));
        content.Children.Add(Card("Authority scope",ReadOnly(decision.Scope,120)));
        if(work!=null)
        {content.Children.Add(Card("Changed files",ReadOnly(string.Join(Environment.NewLine,work.ChangedPaths),100)));
         content.Children.Add(Card("Diff",ReadOnly(work.Diff,180)));
         foreach(var e in work.Evidence.Where(x=>x.Commit==decision.Candidate))
            content.Children.Add(Card("Validation evidence",Label($"{e.Command} · exit {e.ExitCode} · {e.TestedAt.LocalDateTime:g}\n{e.LogPath}",12)));}
        ContentDialog dialog=null!;
        content.Children.Add(Action("Approve this local release",async()=>{await Commands.Decide(decision.Id,true);dialog.Hide();},"ApproveDecision"));
        content.Children.Add(Action("Decline release",async()=>{await Commands.Decide(decision.Id,false);dialog.Hide();},"RejectDecision"));
        dialog=new ContentDialog{Title="Review local release decision",Content=new ScrollViewer{Content=content,MaxHeight=540},CloseButtonText="Later",XamlRoot=root.XamlRoot};
        AutomationProperties.SetAutomationId(dialog,"DecisionReview");
        await dialog.ShowAsync();
        Refresh();
    }
    static void Detach(UIElement element){if(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) is Panel panel)panel.Children.Remove(element);}
    RuntimeUiCommands Commands=>new(RequireRuntime());
    ProjectRuntime RequireRuntime()=>runtime??throw new InvalidOperationException("Open a project in Settings first.");
    static Task OpenPath(string path)
    {if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("This artifact is not available yet.",path);
     if(Directory.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
     else{var editor=new ProcessStartInfo("notepad.exe"){UseShellExecute=false};editor.ArgumentList.Add(path);Process.Start(editor);}
     return Task.CompletedTask;}
    void Notice(string title,string message,bool error=false)
    {notice.Title=title;notice.Message=message;notice.Severity=error?InfoBarSeverity.Error:InfoBarSeverity.Informational;notice.IsOpen=true;}
    async Task Guard(Func<Task> action)
    {try{await action();Refresh();}catch(Exception e){Notice("Could not complete this action",e.Message,true);}}
    Button Action(string title,Func<Task> action,string id,bool primary=false)
    {var button=new Button{Content=title};if(primary)button.Style=Application.Current.Resources["AccentButtonStyle"] as Style;
     AutomationProperties.SetAutomationId(button,id);AutomationProperties.SetName(button,title);
     ToolTipService.SetToolTip(button,id=="StartTask"?"Send task (Ctrl+Enter)":id=="BackToTasks"?"Back to tasks (Escape)":title);
     button.Click+=async(_,_)=>{button.IsEnabled=false;try{await Guard(action);}finally{button.IsEnabled=true;}};return button;}
    static StackPanel Row(params UIElement[] children)
    {var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};foreach(var child in children)row.Children.Add(child);return row;}
    internal static TextBlock Label(string value,double size=14,bool bold=false)=>new()
    {Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,FontFamily=new FontFamily("Segoe UI"),FontWeight=bold?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal};
    static TextBox ReadOnly(string value,double height)=>new()
    {Text=value.ReplaceLineEndings("\r\n"),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=height,FontFamily=new FontFamily("Consolas")};
    static Border Card(string title,UIElement content)
    {var stack=new StackPanel{Spacing=5};stack.Children.Add(Label(title,13,true));stack.Children.Add(content);return new Border{Child=stack,Padding=new Thickness(10),CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),Background=Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,BorderBrush=Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush};}
}















