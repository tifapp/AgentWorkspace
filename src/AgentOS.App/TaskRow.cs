using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentOS.App;

// A task keeps its controls for its lifetime. Snapshot updates only change text and available actions.
internal sealed class TaskRow
{
    readonly string id;
    readonly Action<string> open;
    readonly Func<string,string,Task> followUp;
    readonly Func<string,Task> stop;
    readonly Func<string,Task> reviewDecision;
    readonly Func<MenuFlyout> menu;
    readonly TextBlock title=MainWindow.Label("",14,true), status=MainWindow.Label("",12,true), result=MainWindow.Label("",12), pending=MainWindow.Label("Release decision pending",12);
    readonly TextBox reply=new(){Header="Follow-up task",PlaceholderText="Describe a new task related to this result",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=58};
    readonly TextBox report=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=250};
    readonly Expander reportExpander=new(){Header="Full result"};
    readonly StackPanel replyArea=new(){Spacing=5};
    readonly Button stopButton=new(){Content="Stop"};
    readonly Button reviewButton=new(){Content="Review decision"};
    readonly Border border;
    WorkStatus lastStatus;
    string lastDetail="";
    public StackPanel Root{get;}=new(){Spacing=0};
    public StackPanel Children{get;}=new(){Spacing=7,Margin=new Thickness(18,4,0,4)};
    public TaskRow(string id,Action<string> open,Func<string,string,Task> followUp,Func<string,Task> stop,Func<string,Task> reviewDecision,Func<MenuFlyout> menu)
    {
        this.id=id;this.open=open;this.followUp=followUp;this.stop=stop;this.reviewDecision=reviewDecision;this.menu=menu;
        var card=new StackPanel{Spacing=5,Padding=new Thickness(10,8,10,8)};
        var first=new Grid{ColumnSpacing=6};
        first.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        first.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        first.Children.Add(title);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        var details=new Button{Content="Details"};
        details.Click+=(_,_)=>open(id);
        AutomationProperties.SetAutomationId(details,"TaskDetails");
        actions.Children.Add(details);
        stopButton.Click+=async(_,_)=>await stop(id);
        AutomationProperties.SetAutomationId(stopButton,"CancelTask");
        actions.Children.Add(stopButton);
        var more=new Button{Content="⋯"};
        AutomationProperties.SetName(more,"More task actions");
        more.Click+=(_,_)=>menu().ShowAt(more);
        actions.Children.Add(more);
        Grid.SetColumn(actions,1);first.Children.Add(actions);
        card.Children.Add(first);card.Children.Add(status);card.Children.Add(result);card.Children.Add(pending);
        reviewButton.Click+=async(_,_)=>await reviewDecision(id);
        AutomationProperties.SetAutomationId(reviewButton,"TaskDecision");card.Children.Add(reviewButton);
        reportExpander.Content=report;
        AutomationProperties.SetAutomationId(reportExpander,"CodexReport");
        card.Children.Add(reportExpander);
        AutomationProperties.SetAutomationId(reply,"FollowUpPrompt");
        var send=new Button{Content="Send follow-up"};
        AutomationProperties.SetAutomationId(send,"SendFollowUp");
        send.Click+=async(_,_)=>{send.IsEnabled=false;try{await followUp(id,reply.Text);}finally{send.IsEnabled=true;}};
        replyArea.Children.Add(reply);replyArea.Children.Add(send);card.Children.Add(replyArea);
        border=new Border{Child=card,CornerRadius=new CornerRadius(6),Padding=new Thickness(2),BorderThickness=new Thickness(1),Background=Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,BorderBrush=Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush};
        border.ContextFlyout=menu();
        Root.Children.Add(border);
        var connector=new Border{BorderThickness=new Thickness(1,0,0,0),Margin=new Thickness(13,0,0,0),Child=Children};
        connector.BorderBrush=Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        Root.Children.Add(connector);
        AutomationProperties.SetAutomationId(Root,"TaskRow_"+id);
    }
    public void ClearReply()=>reply.Text="";
    public void UpdateChildren()=>Children.Visibility=Children.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
    public void Patch(WorkUnit work,bool hasPendingDecision)
    {
        if(lastStatus!=work.Status)border.ContextFlyout=menu();
        title.Text=work.Task.Length>180?work.Task[..177]+"…":work.Task;
        status.Text=work.StatusLabel+" · "+work.CreatedAt.ToLocalTime().ToString("g");
        var explanation=work.Status==WorkStatus.Completed
            ? string.IsNullOrWhiteSpace(work.CodexReport)?"Validated and integrated.":work.CodexReport.Trim()
            : work.Detail;
        if(explanation.Length>260)explanation=explanation[..257]+"…";
        result.Text=explanation;
        pending.Visibility=hasPendingDecision?Visibility.Visible:Visibility.Collapsed;
        reviewButton.Visibility=hasPendingDecision?Visibility.Visible:Visibility.Collapsed;
        stopButton.Visibility=work.IsActive?Visibility.Visible:Visibility.Collapsed;
        reportExpander.Visibility=string.IsNullOrWhiteSpace(work.CodexReport)?Visibility.Collapsed:Visibility.Visible;
        if(report.Text!=work.CodexReport)report.Text=work.CodexReport??"";
        replyArea.Visibility=work.IsActive?Visibility.Collapsed:Visibility.Visible;
        AutomationProperties.SetName(reply,"Follow-up task for "+work.ShortTask);

        if(lastStatus!=work.Status||lastDetail!=work.Detail)
        {
            AutomationProperties.SetName(Root,work.StatusLabel+": "+work.ShortTask);
            AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
            lastStatus=work.Status;lastDetail=work.Detail;
        }
    }
}






