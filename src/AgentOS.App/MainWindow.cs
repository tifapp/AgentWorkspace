using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;using Microsoft.UI.Xaml.Automation;
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
    readonly Grid surface = new() { ColumnSpacing = 0 };
    readonly StackPanel tree = new() { Spacing = 8 };
    readonly ScrollViewer map = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly ScrollViewer detail = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel detailBody = new() { Spacing = 10 }; readonly StackPanel composer = new() { Spacing = 5 }; readonly TextBlock emptyHint = Label("No tasks yet. Describe a change below to start your first task.",13);
    readonly TextBlock projectName = Label("Open a project",18,true), summary = Label("",12); readonly Button projectButton = new(); readonly TextBlock previewBadge = Label("UI preview | sample data",12,true);
    readonly TextBlock detailHeading = Label("",18,true), detailStatus = Label("",13); TextBox? detailReport; string detailConflictFingerprint=""; readonly TextBlock liveTranscript = Label("Open to load agent messages.",12); readonly Expander transcriptExpander = new() { Header = "Live transcript" };
    readonly Button decisions = new() { Content = "Decisions" };
    readonly InfoBar notice = new() { IsOpen = false, IsClosable = true };
    readonly TextBox prompt = new() { Header = "New task", PlaceholderText = "Describe the change and how to check it", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 136 };
    readonly TextBox projectPath = new() { Header = "Local Git project", PlaceholderText = @"C:\Projects\my-project" };
    readonly TextBox validation = new() { Header = "Validation command (PowerShell syntax)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
    readonly CheckBox auto = new() { Content = "Validate and integrate automatically", IsChecked = true };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    DispatcherTimer? previewUpdateTimer;
    readonly Dictionary<string, TaskRow> rows = new();
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","desktop.json");
    ProjectRuntime? runtime; ProjectState? snapshot; string? dataRoot, selected; internal CaptureController? Capture { get; set; } internal ProjectRuntime? NotificationRuntime => runtime;
    long generation = -1; bool closing, detailsOpen, wide, walkthrough, previewUpdated, previewEmpty; readonly bool previewMode = Environment.GetCommandLineArgs().Contains("--ui-preview"); string? selectedBeforeDetails;

    public MainWindow()
    {
        Title = "Agent OS"; AppWindow.Resize(new Windows.Graphics.SizeInt32(1040,760));
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header = new FlowPanel();
        var identity = new StackPanel{Spacing=1};
        projectButton.Content=projectName;projectButton.Click+=async(_,_)=>await Guard(ShowSettings);
        AutomationProperties.SetAutomationId(projectButton,"ProjectChooser");
        identity.Children.Add(projectButton);identity.Children.Add(summary);identity.Children.Add(previewBadge);
        previewBadge.Visibility=Visibility.Collapsed;header.Children.Add(identity);
        var controls = new FlowPanel();
        var settingsButton=Action("Settings",ShowSettings,"Settings");settingsButton.Content="? Settings";AutomationProperties.SetName(settingsButton,"Settings");controls.Children.Add(settingsButton);

        decisions.Click += (_,_) => ShowDecisionMenu();
        AutomationProperties.SetAutomationId(decisions,"PendingDecisions"); controls.Children.Add(decisions);
        if(previewMode)
        {
            var previewUpdate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            previewUpdateTimer = previewUpdate;
            previewUpdate.Tick += (_,_) => { previewUpdate.Stop(); if(!closing)ApplySnapshot(PreviewData.Create(previewUpdated,previewEmpty)); };
            controls.Children.Add(Action("Update sample",()=>{previewUpdated=!previewUpdated;previewUpdate.Stop();previewUpdate.Start();return Task.CompletedTask;},"PreviewUpdate"));
            controls.Children.Add(Action("Toggle empty",()=>{previewEmpty=!previewEmpty;ApplySnapshot(PreviewData.Create(previewUpdated,previewEmpty));return Task.CompletedTask;},"PreviewReset"));
        }
        header.Children.Add(controls);root.Children.Add(header);
        Grid.SetRow(notice,1); root.Children.Add(notice);
        map.Content=tree; AutomationProperties.SetAutomationId(map,"WorkList");
        surface.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        surface.ColumnDefinitions.Add(new(){Width=new GridLength(0)});
        surface.Children.Add(map); detail.Content=detailBody; Grid.SetColumn(detail,1); surface.Children.Add(detail);
        Grid.SetRow(surface,2); root.Children.Add(surface);
        composer.Children.Add(prompt);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        var start=Action("Send",SendTask,"StartTask",true);start.IsEnabled=!previewMode;actions.Children.Add(start);
        var options=new Button{Content="Options",Flyout=new Flyout{Content=auto}};
        AutomationProperties.SetName(options,"Task options: automatic integration"); ToolTipService.SetToolTip(options,"Choose automatic validation and integration"); actions.Children.Add(options);
        composer.Children.Add(actions); Grid.SetRow(composer,3); root.Children.Add(composer);
        root.Background=Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] as Brush;Content=root;
        AutomationProperties.SetAutomationId(prompt,"TaskPrompt");
        ToolTipService.SetToolTip(prompt,"Ctrl+N focuses this field. Ctrl+Enter sends this task.");
        AutomationProperties.SetAutomationId(projectPath,"ProjectPath");
        AutomationProperties.SetAutomationId(validation,"ValidationCommand");
        root.SizeChanged+=(_,e)=>{wide=e.NewSize.Width>=1000; ArrangeDetails();};
        Shortcut(VirtualKey.N,VirtualKeyModifiers.Control,()=>{if(detailsOpen&&!wide)CloseDetails();prompt.Focus(FocusState.Programmatic);});
        Shortcut(VirtualKey.Enter,VirtualKeyModifiers.Control,()=>{if(prompt.FocusState!=FocusState.Unfocused&&!previewMode)_=Guard(SendTask);});
        Shortcut(VirtualKey.Escape,VirtualKeyModifiers.None,()=>{CloseDetails();notice.IsOpen=false;});
        AppWindow.Closing+=async (_,e)=>{if(closing)return; if(NotificationController.Current?.TryHideOnClosing()==true){e.Cancel=true;return;} e.Cancel=true; closing=true; timer.Stop(); previewUpdateTimer?.Stop(); if(runtime!=null)await runtime.DisposeAsync(); Close();};
        timer.Tick+=async(_,_)=>{Refresh();if(detailsOpen&&transcriptExpander.IsExpanded)await LoadTranscript();}; timer.Start(); transcriptExpander.Content=liveTranscript;transcriptExpander.Expanding+=async(_,_)=>await LoadTranscript();
        root.Loaded+=async (_,_)=>await Guard(Initialize);
    }
    void Shortcut(VirtualKey key,VirtualKeyModifiers modifiers,Action action)
    {var a=new KeyboardAccelerator{Key=key,Modifiers=modifiers}; a.Invoked+=(_,e)=>{action();e.Handled=true;}; root.KeyboardAccelerators.Add(a);}
    async Task Initialize()
    {
        var args=Environment.GetCommandLineArgs(); if(previewMode){previewBadge.Visibility=Visibility.Visible;projectName.Text="Sample project";ApplySnapshot(PreviewData.Create());return;}
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
        runtime=await ProjectRuntime.OpenAsync(projectPath.Text,dataRoot); runtime.CooperativeExitRequested += () => DispatcherQueue.TryEnqueue(() => Close()); snapshot=runtime.Snapshot;
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
        if(previewMode){await ShowPreviewSettings();return;} var body=new StackPanel{Spacing=12};
        ContentDialog? settingsDialog=null;var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Detach(projectPath);Detach(validation);body.Children.Add(projectPath);
        body.Children.Add(Row(Action("Browse...",Browse,"BrowseProject"),Action("Open project",OpenProject,"OpenProject")));
        body.Children.Add(validation);
        body.Children.Add(Action("Save setup",()=>{RequireRuntime().Configure(validation.Text,HostDiscovery.FindCodex());Notice("Setup saved","Validation command saved.");return Task.CompletedTask;},"SaveSetup"));
        body.Children.Add(Label("A local Git project, Git, and an authenticated Codex CLI are required. Tasks start from committed state.",12));
        var checks=new StackPanel{Spacing=5};
        body.Children.Add(new Expander{Header="Prerequisites",Content=checks});
        body.Children.Add(Action("Check prerequisites",async()=>{checks.Children.Clear();foreach(var c in await HostDiscovery.CheckAsync())checks.Children.Add(Label((c.Ready?"Ready: ":"Action needed: ")+c.Name+" | "+c.Detail,12));},"CheckPrerequisites"));
        var advanced=new StackPanel{Spacing=8};
        advanced.Children.Add(Label(ProjectRuntime.Coverage,12));
        advanced.Children.Add(Action("Notification preferences and history",async()=>{settingsDialog?.Hide();await closed.Task;if(NotificationController.Current is { } notifications)notifications.ShowHistory();else ShowNotificationStatus("Notifications are unavailable.");},"NotificationHistory"));
        advanced.Children.Add(Action("Maps",async()=>{settingsDialog?.Hide();await closed.Task;await ShowMaps();},"TaskMaps"));
        advanced.Children.Add(Action("Capture shortcut: Ctrl+Alt+Space",()=>{(Capture ?? throw new InvalidOperationException("Capture controller unavailable.")).ConfigureHotkey(0x0003,0x20);return Task.CompletedTask;},"CaptureHotkeyDefault"));
        advanced.Children.Add(Action("Capture shortcut: Ctrl+Shift+Space",()=>{(Capture ?? throw new InvalidOperationException("Capture controller unavailable.")).ConfigureHotkey(0x0006,0x20);return Task.CompletedTask;},"CaptureHotkeyAlternate"));
        advanced.Children.Add(Action("Capture shortcut: Ctrl+Alt+M",()=>{(Capture ?? throw new InvalidOperationException("Capture controller unavailable.")).ConfigureHotkey(0x0003,0x4D);return Task.CompletedTask;},"CaptureHotkeyThird"));
        advanced.Children.Add(Action("Capture context into map",async()=>{if(Capture==null)throw new InvalidOperationException("Capture is unavailable.");settingsDialog?.Hide();await closed.Task;await Capture.OpenAsync();},"CaptureNewMap"));
        advanced.Children.Add(Action("Work interactions",async()=>{settingsDialog?.Hide();await closed.Task;await new WorkInteractionsDialog(RequireRuntime(),root.XamlRoot).ShowAsync();},"WorkInteractions"));
        advanced.Children.Add(Action("External effects",()=>ExternalEffectsDialog.OpenAsync(RequireRuntime()),"ExternalEffects"));
        advanced.Children.Add(Action("Native SDK execution profile",async()=>{settingsDialog?.Hide();await closed.Task;await ExecutionProfileDialog.ShowAsync(root.XamlRoot,RequireRuntime().DataDirectory);},"ExecutionProfile"));
        advanced.Children.Add(Action("Reconcile SDK VM receipt",async()=>{settingsDialog?.Hide();await closed.Task;await ExecutionProfileDialog.ReconcileAsync(root.XamlRoot,RequireRuntime());},"SdkReconcile"));
        advanced.Children.Add(Action("Create practice project",CreatePractice,"CreatePractice"));
        advanced.Children.Add(Action("Run concurrency check",Walkthrough,"RunWalkthrough"));
        advanced.Children.Add(Action("Open evidence folder",()=>OpenPath(RequireRuntime().DataDirectory),"OpenEvidenceFolder"));
        advanced.Children.Add(Action("Open project folder",()=>OpenPath(RequireRuntime().Snapshot.ProjectPath),"OpenProjectFolder"));
        body.Children.Add(new Expander{Header="Advanced: coverage, practice and concurrency",Content=advanced});
        settingsDialog=new ContentDialog{Title="Project settings",Content=new ScrollViewer{Content=body,MaxHeight=540,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},CloseButtonText="Close",XamlRoot=root.XamlRoot};
        try{await settingsDialog.ShowAsync();}finally{body.Children.Remove(projectPath);body.Children.Remove(validation);closed.TrySetResult();}
    }
    async Task ShowPreviewSettings(){var dialog=new ContentDialog{Title="Project settings | UI preview",Content=Label("Sample project. Actions are unavailable in preview.",13),CloseButtonText="Close",XamlRoot=root.XamlRoot};await dialog.ShowAsync();}
    async Task ShowMaps()
    {
        var r=RequireRuntime();
        var body=new StackPanel{Spacing=9};
        body.Children.Add(Label("Saved task maps",18,true));
        body.Children.Add(Label("Choose a map to inspect its graph, tasks, links and citations. Save draft edits before starting selected ready tasks.",12));
        var picker=new ComboBox{Header="Saved maps",MinWidth=300};
        var canvasHost=new ContentControl();
        var titleHost=new ContentControl();
        var taskPicker=new ComboBox{Header="Inspect task",MinWidth=300};
        var detailPanel=new StackPanel{Spacing=6};
        var citations=new StackPanel{Spacing=3};
        var info=Label("Choose a saved map.",12);
        body.Children.Add(picker);body.Children.Add(info);body.Children.Add(titleHost);body.Children.Add(canvasHost);body.Children.Add(taskPicker);body.Children.Add(detailPanel);body.Children.Add(citations);
        TaskMap? current=null; TaskMapCanvas? canvas=null;
        TextBox? title=null,taskTitle=null,taskPrompt=null,acceptance=null;
        CheckBox? selectedBox=null; MapTask? shownTask=null;
        bool HasUnappliedTaskEdits()=>shownTask!=null&&taskTitle!=null&&taskPrompt!=null&&acceptance!=null&&selectedBox!=null&&
            (taskTitle.Text.Trim()!=shownTask.Title||taskPrompt.Text.Trim()!=shownTask.Prompt||acceptance.Text.Trim()!=shownTask.Acceptance||(selectedBox.IsChecked==true)!=shownTask.Selected);
        void LoadTask(MapTask? task)
        {
            detailPanel.Children.Clear();shownTask=task;
            if(task==null)return;
            detailPanel.Children.Add(Label("Task details | "+TaskMapCanvas.TaskStatusLabel(task.Status),15,true));
            taskTitle=new TextBox{Header="Task title",Text=task.Title};
            taskPrompt=new TextBox{Header="Task prompt",Text=task.Prompt,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=75};
            acceptance=new TextBox{Header="Acceptance criteria",Text=task.Acceptance,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=65};
            selectedBox=new CheckBox{Content="Select this task for explicit start",IsChecked=task.Selected};
            foreach(var control in new Control[]{taskTitle,taskPrompt,acceptance}){control.IsEnabled=current?.Status==MapStatus.Draft;detailPanel.Children.Add(control);}
            selectedBox.IsEnabled=current?.Status==MapStatus.Draft||(current?.Status==MapStatus.Active&&task.WorkId==null);detailPanel.Children.Add(selectedBox);
            detailPanel.Children.Add(Label("Attempts: "+(task.WorkIds.Count==0?(task.WorkId??"none"):string.Join(", ",task.WorkIds)),12));
            if(current?.Status==MapStatus.Active&&task.WorkId==null)detailPanel.Children.Add(Action("Apply selection",()=>{task.Selected=selectedBox.IsChecked==true;canvas?.Refresh();info.Text="Selection is local until explicit start.";return Task.CompletedTask;},"ApplyActiveMapSelection"));
            if(current?.Status==MapStatus.Draft)detailPanel.Children.Add(Action("Apply task edits",()=>
            {
                if(current?.Status!=MapStatus.Draft)throw new InvalidOperationException("Only draft maps can be edited.");
                var old=(task.Title,task.Prompt,task.Acceptance,task.Selected);
                task.Title=taskTitle.Text.Trim();task.Prompt=taskPrompt.Text.Trim();task.Acceptance=acceptance.Text.Trim();task.Selected=selectedBox.IsChecked==true;
                try{TaskMapRules.Validate(current);}catch{(task.Title,task.Prompt,task.Acceptance,task.Selected)=old;throw;}
                canvas?.Refresh();info.Text="Unsaved draft edits.";return Task.CompletedTask;
            },"ApplyMapTask"));
        }
        void LoadMap(TaskMap saved)
        {
            current=JsonFormat.Copy(saved);
            title=new TextBox{Header="Map title",Text=current.Title,IsEnabled=current.Status==MapStatus.Draft};
            canvas=new TaskMapCanvas(current);
            canvas.ResolveTaskResult=id=>Task.FromResult(r.Snapshot.Work.FirstOrDefault(x=>x.Id==id));
            canvas.SelectionChanged+=LoadTask;
            canvas.MapChanged+=()=>{if(current.Status==MapStatus.Draft)info.Text="Unsaved draft edits.";else LoadMap(saved);};
            taskPicker.Items.Clear();foreach(var task in current.Tasks)taskPicker.Items.Add(new ComboBoxItem{Content=task.Title+" | "+task.Status,Tag=task.Id});
            canvasHost.Content=canvas;detailPanel.Children.Clear();shownTask=null;titleHost.Content=title;
            info.Text=$"{current.Status} | revision {current.Revision} | {current.Citations.Count} citations. "+(current.Status==MapStatus.Draft?"Select a task to edit it.":"Select unstarted tasks for an explicit start at this revision.");
            citations.Children.Clear();foreach(var citation in current.Citations)citations.Children.Add(Label($"Citation: {citation.Source} | SHA-256 {citation.Sha256}",12));
        }
        void Reload(string? id=null)
        {
            id??=(picker.SelectedItem as ComboBoxItem)?.Tag as string;
            picker.Items.Clear();foreach(var saved in r.Snapshot.Maps.OrderBy(x=>x.Title))picker.Items.Add(new ComboBoxItem{Content=saved.Title+" | "+saved.Status,Tag=saved.Id});
            picker.SelectedItem=picker.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>(string)x.Tag==id);
            if(picker.SelectedItem==null&&picker.Items.Count>0)picker.SelectedIndex=0;
        }
        picker.SelectionChanged+=(_,_)=>{if(picker.SelectedItem is ComboBoxItem entry)LoadMap(r.Snapshot.Maps.Single(x=>x.Id==(string)entry.Tag));};
        taskPicker.SelectionChanged+=(_,_)=>{if(current!=null&&taskPicker.SelectedItem is ComboBoxItem choice)LoadTask(current.Tasks.Single(x=>x.Id==(string)choice.Tag));};
        Reload();
        body.Children.Add(Action("Save draft",()=>
        {
            if(current==null||title==null)throw new ArgumentException("Choose a map.");
            if(current.Status!=MapStatus.Draft)throw new InvalidOperationException("Only draft maps can be saved.");
            if(HasUnappliedTaskEdits())throw new InvalidOperationException("Apply task edits before saving the draft.");
            current.Title=title.Text.Trim();TaskMapRules.Validate(current);
            var id=r.SaveDraftMap(current,current.Revision);Reload(id);
            Notice("Draft saved","No task was started.");return Task.CompletedTask;
        },"SaveMapDraft"));
        body.Children.Add(Action("Start selected ready tasks",async()=>
        {
            if(current==null)throw new ArgumentException("Choose a saved map.");
            if(HasUnappliedTaskEdits())throw new InvalidOperationException("Apply task edits and save the draft before starting.");
            var saved=r.Snapshot.Maps.SingleOrDefault(x=>x.Id==current.Id)??throw new InvalidOperationException("Save this map first.");
            var normalized=JsonFormat.Copy(current);foreach(var task in normalized.Tasks.Where(t=>t.WorkId==null)){var persisted=saved.Tasks.SingleOrDefault(x=>x.Id==task.Id)??throw new InvalidOperationException("Map changed. Reload before starting.");task.Selected=persisted.Selected;}
            if(title?.Text.Trim()!=current.Title || JsonSerializer.Serialize(saved,JsonFormat.Options)!=JsonSerializer.Serialize(normalized,JsonFormat.Options))throw new InvalidOperationException("Save draft edits before starting tasks.");
            var ids=current.Tasks.Where(t=>t.Selected&&t.WorkId==null&&TaskMapRules.DependenciesComplete(saved,t)).Select(t=>t.Id).ToArray();
            var launched=await r.StartSelectedMapTasksAsync(saved.Id,ids,saved.Revision);Notice("Tasks started",string.Join(", ",launched));Reload(saved.Id);
        },"StartMapTasks"));
        await new ContentDialog{Title="Task maps",Content=new ScrollViewer{Content=body,MaxHeight=590},CloseButtonText="Close",XamlRoot=root.XamlRoot}.ShowAsync();
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
    {if(previewMode)return;var value=prompt.Text;var id=await Commands.Start(value,auto.IsChecked==true);prompt.Text="";selected=id;Refresh();}
    async Task SendFollowUp(string parentId,string value)
    {if(previewMode)return;var work=RequireRuntime().Snapshot.Work.Single(x=>x.Id==parentId);
     if(work.IsActive)await Commands.SendSteering(parentId,value);
     else if(work.Status==WorkStatus.Completed)selected=await Commands.ReplyAfterCompletion(parentId,value);
     else throw new InvalidOperationException("Only an active or completed task accepts a reply.");
     rows[parentId].ClearReply();Refresh();}
    void Refresh()
    {
        if(runtime==null)return;
        var state=runtime.Snapshot;if(state.Generation==generation)return;
        ApplySnapshot(state);
    }
    void ApplySnapshot(ProjectState state)
    {
        generation=state.Generation;snapshot=state;
        if(state.Work.Count==0&&detailsOpen)CloseDetails();
        var pending=state.Decisions.Count(x=>x.Status==DecisionStatus.Pending);
        summary.Text=$"{state.Work.Count(x=>x.IsActive)} active | {state.Work.Count(x=>x.Status==WorkStatus.Completed)} completed";
        AutomationProperties.SetLiveSetting(summary,AutomationLiveSetting.Polite);
        decisions.Content=pending==0?"Decisions":$"Decisions ({pending})";decisions.IsEnabled=pending>0&&!previewMode;
        var scrollOffset=map.VerticalOffset;
        var live=state.Work.Select(x=>x.Id).ToHashSet();
        foreach(var id in rows.Keys.Where(x=>!live.Contains(x)).ToArray()){var row=rows[id];if(row.Root.Parent is Panel p)p.Children.Remove(row.Root);rows.Remove(id);}
        foreach(var work in state.Work)
        {
            if(!rows.TryGetValue(work.Id,out var row))
            {row=new TaskRow(work.Id,id=>OpenDetails(id),(id,value)=>Guard(()=>SendFollowUp(id,value)),id=>Guard(()=>{Commands.Stop(id);return Task.CompletedTask;}),id=>Guard(()=>ShowWorkDecision(id)),()=>RowMenu(work.Id),()=>auto.IsChecked==true,value=>auto.IsChecked=value,previewMode);rows.Add(work.Id,row);}
            row.Patch(work,state.Decisions.Any(x=>x.WorkId==work.Id&&x.Status==DecisionStatus.Pending));
        }
        if(!tree.Children.Contains(emptyHint))tree.Children.Add(emptyHint);
        emptyHint.Text=previewMode?"Sample map is empty. Use Toggle empty to restore sample tasks.":"No tasks yet. Describe a change below to start your first task.";
        emptyHint.Visibility=state.Work.Count==0?Visibility.Visible:Visibility.Collapsed;
        Reconcile(tree,state.Work.Where(x=>x.ParentId==null||!rows.ContainsKey(x.ParentId)).Reverse().Select(x=>x.Id).ToArray());
        foreach(var work in state.Work){Reconcile(rows[work.Id].Children,state.Work.Where(x=>x.ParentId==work.Id).Select(x=>x.Id).ToArray());rows[work.Id].UpdateChildren();}
        DispatcherQueue.TryEnqueue(()=>{if(Math.Abs(map.VerticalOffset-scrollOffset)>1)map.ChangeView(null,scrollOffset,null,true);});
        if(detailsOpen){var shown=state.Work.FirstOrDefault(x=>x.Id==selected);if(shown!=null){if(ConflictFingerprint(state,shown)!=detailConflictFingerprint)DrawDetails();detailHeading.Text=(shown.Relationship==WorkRelationship.Revision?"Revision: ":"")+shown.StatusLabel+" | "+shown.ShortTask;detailStatus.Text=shown.Detail;if(detailReport!=null&&detailReport.Text!=(shown.CodexReport??""))detailReport.Text=shown.CodexReport??"";}}
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
    void OpenDetails(string id){selected=id;selectedBeforeDetails=id;detailsOpen=true;DrawDetails();ArrangeDetails();} void CloseDetails(){detailsOpen=false;ArrangeDetails();if(selectedBeforeDetails!=null&&rows.TryGetValue(selectedBeforeDetails,out var row))row.FocusDetails();}
    void ArrangeDetails()
    {
        surface.ColumnDefinitions[0].Width=new GridLength(detailsOpen?(wide?0.55:0):1,GridUnitType.Star);
        surface.ColumnDefinitions[1].Width=new GridLength(detailsOpen?(wide?0.45:1):0,GridUnitType.Star);
        surface.ColumnSpacing=detailsOpen&&wide?12:0;
        map.Visibility=detailsOpen&&!wide?Visibility.Collapsed:Visibility.Visible; composer.Visibility=detailsOpen&&!wide?Visibility.Collapsed:Visibility.Visible;
        detail.Visibility=detailsOpen?Visibility.Visible:Visibility.Collapsed;
    }

    static string ConflictFingerprint(ProjectState state, WorkUnit work)
    {
        var conflicts=state.Conflicts.Where(x=>x.WorkId==work.Id).ToArray();
        return conflicts.Length==0?"":JsonSerializer.Serialize(new { work.Status, Conflicts=conflicts, Escalations=state.Escalations.Where(x=>x.WorkId==work.Id).ToArray() });
    }
    void DrawDetails()
    {
        detailBody.Children.Clear();detailReport=null;
        var work=snapshot?.Work.FirstOrDefault(x=>x.Id==selected);if(work==null)return; detailConflictFingerprint=ConflictFingerprint(snapshot!,work);
        detailBody.Children.Add(Row(Action("Back",()=>{CloseDetails();return Task.CompletedTask;},"BackToTasks"),Action("Refresh details",()=>{DrawDetails();return Task.CompletedTask;},"RefreshDetails")));
        detailHeading.Text=(work.Relationship==WorkRelationship.Revision?"Revision: ":"")+work.StatusLabel+" | "+work.ShortTask;detailBody.Children.Add(detailHeading);
        detailStatus.Text=work.Detail;detailBody.Children.Add(detailStatus);
        if(work.ParentId!=null&&snapshot!.Work.FirstOrDefault(x=>x.Id==work.ParentId) is { } original)
            detailBody.Children.Add(Action("Parent task",()=>{OpenDetails(original.Id);return Task.CompletedTask;},"OriginalTask"));
        foreach(var child in snapshot!.Work.Where(x=>x.ParentId==work.Id))
            detailBody.Children.Add(Action("Child task | "+child.StatusLabel,()=>{OpenDetails(child.Id);return Task.CompletedTask;},"RevisionTask"));
        detailBody.Children.Add(Card("Full prompt",ReadOnly(work.Task,200))); if(work.IsActive&&!previewMode){var steering=new TextBox{Header="Reply to active task",PlaceholderText="Send guidance to the current turn",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=65};AutomationProperties.SetAutomationId(steering,"ActiveTaskSteering");detailBody.Children.Add(steering);detailBody.Children.Add(Action("Send steering",async()=>{await Commands.SendSteering(work.Id,steering.Text);steering.Text="";},"SendSteering"));detailBody.Children.Add(transcriptExpander);}
        foreach(var conflict in snapshot!.Conflicts.Where(x=>x.WorkId==work.Id))
        {
            var panel=new StackPanel{Spacing=5};
            var state=conflict.Abandoned?"Abandoned":conflict.Resolved?"Resolved":conflict.Response==null?"Needs response":"Parked unresolved";
            panel.Children.Add(Label($"{state} - {conflict.Id}",14,true));
            panel.Children.Add(ReadOnly($"Cause: {conflict.Cause}\nPaths: {string.Join(", ",conflict.Paths)}\nBase commit: {conflict.BaseCommit}\nCurrent commit: {conflict.CurrentCommit}\nHolder: {conflict.HolderWorkId??"Unknown"}\nRetained candidate: {conflict.DeferredCandidateCommit??"Unknown"}\nResponse: {conflict.Response??"Owed"}\nPublication blocked: {conflict.PublicationBlocked}\nResolution: {conflict.ResolutionExplanation??(conflict.Abandoned?"Abandoned":"Unresolved")}",190));
            if(!snapshot.Escalations.Any(x=>x.ConflictId==conflict.Id))panel.Children.Add(Label("Human escalation: None",12));
            foreach(var escalation in snapshot.Escalations.Where(x=>x.ConflictId==conflict.Id))
                panel.Children.Add(Label("Human escalation: "+escalation.Explanation,12));
            if(!previewMode&&!conflict.Resolved&&!conflict.Abandoned&&conflict.Response==null&&(work.Status is WorkStatus.NeedsResponse or WorkStatus.Parked))
                panel.Children.Add(Action("Resume conflict response",async()=>{await Commands.ResumeConflict(work.Id);Refresh();},"ResumeConflictResponse"));
            detailBody.Children.Add(Card("Conflict",panel));
        }
        if(!string.IsNullOrWhiteSpace(work.CodexReport)){detailReport=ReadOnly(work.CodexReport,300);detailBody.Children.Add(Card("Full report",detailReport));}
        if(work.ChangedPaths.Count>0)detailBody.Children.Add(Card("Changed files",ReadOnly(string.Join(Environment.NewLine,work.ChangedPaths),160)));
        if(!string.IsNullOrWhiteSpace(work.Diff))detailBody.Children.Add(Card("Candidate diff",ReadOnly(work.Diff,300)));
        if(work.IntegratedCommit!=null)detailBody.Children.Add(Label("Integrated commit: "+work.IntegratedCommit,12));
        foreach(var evidence in work.Evidence)
        {var panel=new StackPanel{Spacing=5};
         panel.Children.Add(Label($"{evidence.TestedAt.LocalDateTime:g} | exit {evidence.ExitCode} | {(evidence.Passed?"passed":"failed")}",12));
         panel.Children.Add(ReadOnly($"Commit {evidence.Commit}\nTree {evidence.Tree}\nBased on {evidence.AgainstCommit}\n{evidence.Command}\n{evidence.Environment}",170));
         if(!previewMode)panel.Children.Add(Action("Open validation log",()=>OpenPath(evidence.LogPath),"OpenValidationLog"));
         detailBody.Children.Add(Card("Validation evidence",panel));}
        detailBody.Children.Add(Label("Recent activity",15,true));
        foreach(var entry in snapshot!.Events.Where(x=>x.WorkId==work.Id).TakeLast(20).Reverse())
            detailBody.Children.Add(Label(entry.At.ToLocalTime().ToString("g")+" | "+entry.Message,12));
    }
    async Task LoadTranscript(){if(previewMode||selected==null||!detailsOpen||!transcriptExpander.IsExpanded)return;var id=selected;var text=await TranscriptReader.ReadAsync(Commands.Transcript(id));if(selected==id&&liveTranscript.Text!=text)liveTranscript.Text=text;}
    MenuFlyout RowMenu(string id)
    {
        var menu=new MenuFlyout();var work=snapshot?.Work.FirstOrDefault(x=>x.Id==id);if(work==null)return menu;
        void Add(string title,Func<Task> action,string automationId)
        {var item=new MenuFlyoutItem{Text=title};AutomationProperties.SetAutomationId(item,automationId);item.Click+=async(_,_)=>await Guard(action);menu.Items.Add(item);}
        if(previewMode)return menu; if(work.IsActive)Add("Stop task",()=>{Commands.Stop(id);return Task.CompletedTask;},"CancelTask");
        if(snapshot?.Conflicts.Any(x=>x.WorkId==id&&!x.Resolved&&!x.Abandoned)!=true&&work.Status==WorkStatus.Private)Add("Integrate candidate",()=>Commands.Integrate(id),"IntegrateCandidate");
        if(snapshot?.Conflicts.Any(x=>x.WorkId==id&&!x.Resolved&&!x.Abandoned)!=true&&!work.IsActive&&work.Status!=WorkStatus.Completed)Add("Revise with Codex",async()=>{selected=await Commands.Revise(id);Refresh();},"ReviseTask");
        if(snapshot?.Conflicts.Any(x=>x.WorkId==id&&!x.Resolved&&!x.Abandoned&&x.Response==null)==true&&(work.Status is WorkStatus.NeedsResponse or WorkStatus.Parked))
            Add("Resume conflict response",()=>Commands.ResumeConflict(id),"ResumeConflictResponse");
        Add("Interactions",()=>new WorkInteractionsDialog(RequireRuntime(),root.XamlRoot,id).ShowAsync(),"TaskInteractions");
        if(snapshot?.Conflicts.Any(x=>x.WorkId==id&&!x.Resolved&&!x.Abandoned)!=true&&work.Status==WorkStatus.Completed)Add("Prepare release decision",async()=>{await Commands.PrepareRelease(id);Refresh();},"PrepareRelease");
        if(Directory.Exists(work.Workspace))Add("Open private files",()=>OpenPath(work.Workspace),"OpenPrivate");
        Add("Open transcript",()=>OpenPath(Commands.Transcript(id)),"OpenTranscript");
        if(File.Exists(Commands.Diagnostics(id)))Add("Runtime diagnostics",()=>OpenPath(Commands.Diagnostics(id)),"OpenDiagnostics");
        if(snapshot?.Conflicts.Any(x=>x.WorkId==id&&!x.Resolved&&!x.Abandoned)!=true&&!work.IsActive&&!work.WorkspaceRemoved)Add("Clean private files",()=>Commands.Cleanup(id),"CleanupTask");
        return menu;
    }
    void ShowDecisionMenu()
    {
        var menu=new MenuFlyout();
        foreach(var choice in snapshot?.Decisions.Where(x=>x.Status==DecisionStatus.Pending).Reverse()??[])
        {
            var item=new MenuFlyoutItem{Text=(snapshot?.Work.FirstOrDefault(x=>x.Id==choice.WorkId)?.ShortTask??"Task")+" | "+choice.Candidate[..Math.Min(8,choice.Candidate.Length)]};
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
            content.Children.Add(Card("Validation evidence",Label($"{e.Command} | exit {e.ExitCode} | {e.TestedAt.LocalDateTime:g}\n{e.LogPath}",12)));}
        ContentDialog dialog=null!;
        content.Children.Add(Action("Approve this local release",async()=>{await Commands.Decide(decision.Id,true);dialog.Hide();},"ApproveDecision"));
        content.Children.Add(Action("Decline release",async()=>{await Commands.Decide(decision.Id,false);dialog.Hide();},"RejectDecision"));
        dialog=new ContentDialog{Title="Review local release decision",Content=new ScrollViewer{Content=content,MaxHeight=540},CloseButtonText="Later",XamlRoot=root.XamlRoot};
        AutomationProperties.SetAutomationId(dialog,"DecisionReview");
        await dialog.ShowAsync();
        Refresh();
    }
    static void Detach(UIElement element){var parent=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);if(parent is Panel panel)panel.Children.Remove(element);else if(parent is ContentControl control)control.Content=null;}
    RuntimeUiCommands Commands=>new(RequireRuntime());
    ProjectRuntime RequireRuntime()=>runtime??throw new InvalidOperationException("Open a project in Settings first.");
    static Task OpenPath(string path)
    {if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("This artifact is not available yet.",path);
     if(Directory.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
     else{var editor=new ProcessStartInfo("notepad.exe"){UseShellExecute=false};editor.ArgumentList.Add(path);Process.Start(editor);}
     return Task.CompletedTask;}
    internal void ShowNotificationStatus(string message)=>Notice("Notification",message);
    internal bool NavigateNotice(LocalNotice item)
    {
        var active=runtime;
        if(previewMode||active==null||!string.Equals(active.Snapshot.ProjectPath,item.Project,StringComparison.OrdinalIgnoreCase))return false;
        Refresh();var state=active.Snapshot;
        if(item.Identity.StartsWith("effect:",StringComparison.Ordinal))
        {if(item.DecisionId==null||item.Identity!="effect:"+item.DecisionId||active.InspectExternalEffect(item.DecisionId)?.State!=ExternalEffectState.Prepared)return false;_ = Guard(()=>ExternalEffectsDialog.OpenAsync(active));return true;}
        if(item.WorkId==null)return false;
        var work=state.Work.FirstOrDefault(x=>x.Id==item.WorkId);if(work==null)return false;
        if(item.Identity.StartsWith("decision:",StringComparison.Ordinal))
        {if(!state.Decisions.Any(x=>x.Id==item.DecisionId&&x.WorkId==work.Id&&x.Status==DecisionStatus.Pending&&item.Identity=="decision:"+x.Id))return false;OpenDetails(work.Id);_ = Guard(()=>ShowDecision(item.DecisionId!));return true;}
        if(item.Identity.StartsWith("interaction:",StringComparison.Ordinal))
        {if(item.DecisionId==null||!active.InspectInteractions().Any(x=>x.Id==item.DecisionId&&x.Status==InteractionStatus.Pending&&(!x.Deadline.HasValue||x.Deadline>DateTimeOffset.UtcNow)&&item.Identity=="interaction:"+x.Kind+":"+x.Id&&item.WorkId==(x.Kind==InteractionKind.Peer?x.TargetWorkId:x.WorkId)&&(x.Kind is InteractionKind.Clarification or InteractionKind.Peer or InteractionKind.Followup||x.Kind==InteractionKind.Obligation&&x.Required)))return false;OpenDetails(work.Id);_ = Guard(()=>new WorkInteractionsDialog(active,root.XamlRoot,work.Id).ShowAsync());return true;}
        if(item.Identity.StartsWith("conflict:",StringComparison.Ordinal))
        {if(!state.Conflicts.Any(x=>x.Id==item.DecisionId&&x.WorkId==work.Id&&!x.Resolved&&!x.Abandoned&&x.Response==null&&item.Identity=="conflict:"+x.Id)||work.Status is not (WorkStatus.NeedsResponse or WorkStatus.Parked))return false;OpenDetails(work.Id);return true;}
        if(item.Identity!=$"work:{work.Id}:{work.Status}:{work.UpdatedAt.UtcTicks}"||work.Status.ToString()!=item.Kind)return false;
        OpenDetails(work.Id);return true;
    }
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
