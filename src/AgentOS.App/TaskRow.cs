using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace AgentOS.App;
internal sealed class TaskRow
{
 readonly Func<string,string,Task> followUp; readonly Func<MenuFlyout> menu; readonly bool preview;
 readonly TextBlock title=MainWindow.Label("",13,true),status=MainWindow.Label("",12),snippet=MainWindow.Label("",12),pending=MainWindow.Label("Release decision pending",12);
 readonly TextBox reply=new(){Header="Follow-up task",PlaceholderText="Describe the next change",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=58};
 readonly TextBox report=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=250};
 readonly FontIcon stateIcon=new(){FontFamily=new FontFamily("Segoe Fluent Icons"),Width=14,Height=14};
 readonly Expander reportExpander=new(); readonly StackPanel replyArea=new(){Spacing=5};
 readonly Button details=new(){Content="Details"},replyStub=new(){Content="Reply"},stopButton=new(){Content="Stop"},reviewButton=new(){Content="Review decision"},send=new(){Content="Send follow-up"};
 bool sending; WorkStatus lastStatus;
 public StackPanel Root{get;}=new(); public StackPanel Children{get;}=new(){Spacing=5,Margin=new Thickness(16,2,0,3)};
 public TaskRow(string id,Action<string> open,Func<string,string,Task> followUp,Func<string,Task> stop,Func<string,Task> reviewDecision,Func<MenuFlyout> menu,Func<bool> autoChoice,Action<bool> setAutoChoice,bool preview)
 {
  this.followUp=followUp;this.menu=menu;this.preview=preview;
  var card=new StackPanel{Spacing=3,Padding=new Thickness(7,4,7,4)};
  var top=new Grid{ColumnSpacing=5};top.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});top.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  var prompt=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};var chat=new SymbolIcon(Symbol.Message){Width=16,Height=16};AutomationProperties.SetName(chat,"Task prompt");prompt.Children.Add(chat);prompt.Children.Add(title);top.Children.Add(prompt);
  var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3};AutomationProperties.SetAutomationId(details,"TaskDetails");details.Click+=(_,_)=>open(id);actions.Children.Add(details);
  AutomationProperties.SetAutomationId(stopButton,"CancelTask");stopButton.Click+=async(_,_)=>await stop(id);actions.Children.Add(stopButton);
  var more=new Button{Content="⋯"};AutomationProperties.SetAutomationId(more,"TaskActions");AutomationProperties.SetName(more,"More task actions");more.Click+=(_,_)=>menu().ShowAt(more);actions.Children.Add(more);Grid.SetColumn(actions,1);top.Children.Add(actions);card.Children.Add(top);
  var state=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};state.Children.Add(stateIcon);state.Children.Add(status);card.Children.Add(state);
  var resultBody=new StackPanel{Spacing=5};resultBody.Children.Add(report);resultBody.Children.Add(replyArea);reportExpander.Header=snippet;reportExpander.Content=resultBody;AutomationProperties.SetAutomationId(reportExpander,"CodexReport");
  card.Children.Add(new Border{BorderThickness=new Thickness(1,0,0,0),Margin=new Thickness(10,0,0,0),Padding=new Thickness(7,0,0,0),BorderBrush=Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,Child=reportExpander});
  card.Children.Add(pending);AutomationProperties.SetAutomationId(reviewButton,"TaskDecision");reviewButton.Click+=async(_,_)=>await reviewDecision(id);card.Children.Add(reviewButton);
  AutomationProperties.SetAutomationId(replyStub,"OpenFollowUp");replyStub.Click+=(_,_)=>OpenReply();card.Children.Add(new Border{BorderThickness=new Thickness(1,0,0,0),Margin=new Thickness(10,0,0,0),Padding=new Thickness(7,0,0,0),BorderBrush=Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,Child=replyStub});
  AutomationProperties.SetAutomationId(reply,"FollowUpPrompt");ToolTipService.SetToolTip(reply,"Ctrl+Enter sends; Enter adds a line.");var shortcut=new KeyboardAccelerator{Key=VirtualKey.Enter,Modifiers=VirtualKeyModifiers.Control};shortcut.Invoked+=async(_,e)=>{e.Handled=true;await Send();};reply.KeyboardAccelerators.Add(shortcut);
  AutomationProperties.SetAutomationId(send,"SendFollowUp");ToolTipService.SetToolTip(send,"Send follow-up (Ctrl+Enter)");send.Click+=async(_,_)=>await Send();replyArea.Children.Add(reply);
  var choices=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};choices.Children.Add(send);var integrate=new CheckBox{Content="Validate and integrate automatically",IsChecked=autoChoice()};integrate.Checked+=(_,_)=>setAutoChoice(true);integrate.Unchecked+=(_,_)=>setAutoChoice(false);
  choices.Children.Add(new Button{Content="Options",Flyout=new Flyout{Content=integrate},IsEnabled=!preview});replyArea.Children.Add(choices);replyArea.Visibility=Visibility.Collapsed;
  var border=new Border{Child=card,CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(0,0,0,1),BorderBrush=Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush};
  var context=new MenuFlyout();context.Opening+=(_,_)=>{var fresh=menu();context.Items.Clear();while(fresh.Items.Count>0){var item=fresh.Items[0];fresh.Items.RemoveAt(0);context.Items.Add(item);}};border.ContextFlyout=context;Root.Children.Add(border);
  Root.Children.Add(new Border{BorderThickness=new Thickness(1,0,0,0),Margin=new Thickness(12,0,0,0),Child=Children,BorderBrush=Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush});AutomationProperties.SetAutomationId(Root,"TaskRow_"+id);
 }
 void OpenReply(){reportExpander.IsExpanded=true;replyArea.Visibility=Visibility.Visible;replyStub.Visibility=Visibility.Collapsed;reply.Focus(FocusState.Programmatic);}
 async Task Send(){if(preview||sending||string.IsNullOrWhiteSpace(reply.Text))return;sending=true;send.IsEnabled=false;try{await followUp(id,reply.Text);}finally{sending=false;send.IsEnabled=!preview;}}
 public void FocusDetails()=>details.Focus(FocusState.Programmatic);
 public void ClearReply(){reply.Text="";replyArea.Visibility=Visibility.Collapsed;replyStub.Visibility=Visibility.Visible;}
 public void UpdateChildren()=>Children.Visibility=Children.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
 public void Patch(WorkUnit work,bool decision)
 {
  title.Text=work.ShortTask;status.Text=work.StatusLabel+" · "+work.CreatedAt.ToLocalTime().ToString("g");
  stateIcon.Glyph=work.Status switch{WorkStatus.Completed=>"\uE73E",WorkStatus.Failed or WorkStatus.Canceled=>"\uEA39",WorkStatus.Waiting=>"\uE916",WorkStatus.Stale or WorkStatus.Unknown=>"\uE897",WorkStatus.Private=>"\uE8A7",_=>"\uE895"};AutomationProperties.SetName(stateIcon,work.StatusLabel);
  var value=(work.Status==WorkStatus.Completed?work.CodexReport:work.Detail)?.Trim();if(string.IsNullOrWhiteSpace(value))value="No result yet.";value=string.Join(" ",value.Split('\n').Take(2)).Trim();snippet.Text=value.Length>180?value[..177]+"…":value;
  pending.Visibility=decision?Visibility.Visible:Visibility.Collapsed;reviewButton.Visibility=decision?Visibility.Visible:Visibility.Collapsed;reviewButton.IsEnabled=!preview;
  stopButton.Visibility=work.IsActive&&!preview?Visibility.Visible:Visibility.Collapsed;var full=work.CodexReport??work.Detail;if(report.Text!=full)report.Text=full;
  if(work.IsActive)replyArea.Visibility=Visibility.Collapsed;replyStub.Visibility=!work.IsActive&&replyArea.Visibility==Visibility.Collapsed?Visibility.Visible:Visibility.Collapsed;send.IsEnabled=!preview&&!sending;
  AutomationProperties.SetName(reply,"Follow-up task for "+work.ShortTask);if(lastStatus!=work.Status){AutomationProperties.SetName(Root,work.StatusLabel+": "+work.ShortTask);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);lastStatus=work.Status;}
 }
}

