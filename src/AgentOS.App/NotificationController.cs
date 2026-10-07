using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinRT.Interop;

namespace AgentOS.App;
public sealed class NotificationController:IDisposable
{
 const uint Callback=0x8001,IconId=1,SubclassId=0xA606;
 const uint NimAdd=0,NimModify=1,NimDelete=2,NimSetVersion=4,NifMessage=1,NifIcon=2,NifTip=4,NifInfo=0x10;
 const uint WmContextMenu=0x7B,WmLButtonUp=0x202,WmLButtonDblClk=0x203,WmRButtonUp=0x205,WmClose=0x10;
 readonly MainWindow window;readonly CaptureController capture;readonly nint hwnd;readonly SubclassProc procedure;readonly uint taskbarCreated;
 readonly NotificationCenter center;readonly ProjectNotificationWatcher watcher;readonly Microsoft.UI.Dispatching.DispatcherQueue queue;
 readonly System.Threading.Timer settingsTimer;string? selectedProject,selectedRoot;bool keepRunning,exiting,disposed,iconAdded,subclassAttached;Window? historyWindow;LocalNotice? lastBalloon;
 public static NotificationController? Current {get;private set;}
 public bool TrayAvailable=>iconAdded;
 public Func<LocalNotice,bool>? NavigateNotice {get;set;}
 public NotificationController(MainWindow window,CaptureController capture)
 {this.window=window;this.capture=capture;queue=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();hwnd=WindowNative.GetWindowHandle(window);
  center=new NotificationCenter();watcher=new ProjectNotificationWatcher(center,n=>queue.TryEnqueue(()=>Deliver(n)));
  procedure=WindowMessage;taskbarCreated=RegisterWindowMessage("TaskbarCreated");
  LoadPreferences();subclassAttached=hwnd!=0&&SetWindowSubclass(hwnd,procedure,SubclassId,0);
  if(subclassAttached)AddIcon();if(!iconAdded){keepRunning=false;window.Title="Agent OS (notifications unavailable)";}
  window.AppWindow.Closing+=(_,e)=>{if(keepRunning&&iconAdded&&!exiting){e.Cancel=true;window.AppWindow.Hide();}};
  settingsTimer=new System.Threading.Timer(_=>queue.TryEnqueue(RefreshProject),null,0,1000);
  Current=this;window.Closed+=(_,_)=>Dispose();
 }
 readonly string preferencePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","notification-settings.json");
 void LoadPreferences()
 {try{if(!File.Exists(preferencePath))return;using var doc=JsonDocument.Parse(File.ReadAllText(preferencePath));if(doc.RootElement.TryGetProperty("keepRunning",out var keep))keepRunning=keep.GetBoolean();}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){} }
 void SavePreferences()
 {try{Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);var next=preferencePath+".next";File.WriteAllText(next,JsonSerializer.Serialize(new {keepRunning}));File.Move(next,preferencePath,true);}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException){} }
 public bool TryHideOnClosing()
 {if(!keepRunning||!iconAdded||exiting)return false;window.AppWindow.Hide();return true;}
 void RefreshProject()
 {if(disposed)return;try{var active=window.NotificationRuntime;var project=active?.Snapshot.ProjectPath;var dir=active?.DataDirectory;
   if(string.Equals(selectedProject,project,StringComparison.OrdinalIgnoreCase)&&string.Equals(selectedRoot,dir,StringComparison.OrdinalIgnoreCase))return;
   watcher.Select(project,null,dir);selectedProject=project;selectedRoot=dir;
  }catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){window.ShowNotificationStatus("Notification scanning is unavailable.");}}
 void Deliver(LocalNotice notice)
 {try{if(disposed||!iconAdded||!center.Enabled||center.IsQuiet(DateTimeOffset.UtcNow)||!watcher.IsCurrent(notice))return;var data=BaseIcon();data.uFlags=NifInfo;data.szInfoTitle="Agent OS - "+notice.Kind;data.szInfo=notice.Title;data.dwInfoFlags=1;if(Shell_NotifyIcon(NimModify,ref data)){lastBalloon=notice;center.MarkDelivered(notice);}}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){window.ShowNotificationStatus("Notification delivery is unavailable.");}}
 void AddIcon()
 {var data=BaseIcon();data.uFlags=NifMessage|NifIcon|NifTip;iconAdded=Shell_NotifyIcon(NimAdd,ref data);if(iconAdded){data.uVersion=4;Shell_NotifyIcon(NimSetVersion,ref data);}}
 NOTIFYICONDATA BaseIcon()=>new(){cbSize=(uint)Marshal.SizeOf<NOTIFYICONDATA>(),hWnd=hwnd,uID=IconId,uCallbackMessage=Callback,hIcon=LoadIcon(0,(nint)32512),szTip="Agent OS",szInfo="",szInfoTitle=""};
 nint WindowMessage(nint h,uint message,nuint wp,nint lp,nuint id,nuint data)
 {if(message==taskbarCreated){AddIcon();return 0;}
  if(message==WmClose&&keepRunning&&iconAdded&&!exiting){queue.TryEnqueue(()=>window.AppWindow.Hide());return 0;}
  if(message==Callback){var eventCode=(uint)((long)lp&0xffff);if(eventCode==0x0405){var notice=lastBalloon;if(notice!=null)queue.TryEnqueue(()=>OpenNotice(notice));return 0;}if(eventCode is WmRButtonUp or WmContextMenu){queue.TryEnqueue(ShowMenu);return 0;}
   if(eventCode is WmLButtonUp or WmLButtonDblClk){queue.TryEnqueue(ShowWorkspace);return 0;}}
  return DefSubclassProc(h,message,wp,lp);}
 void ShowWorkspace(){window.AppWindow.Show();window.Activate();SetForegroundWindow(hwnd);}
 void StopExit(){exiting=true;keepRunning=false;ShowWorkspace();window.Close();}
 void ShowMenu()
 {var menu=CreatePopupMenu();if(menu==0)return;try{
   AppendMenu(menu,0,1,"Show workspace");AppendMenu(menu,0,2,"Capture context");AppendMenu(menu,0,3,"Notification history");
   AppendMenu(menu,0x800,0,null);AppendMenu(menu,keepRunning?0x8u:0u,4,"Keep running in tray");AppendMenu(menu,0x800,0,null);AppendMenu(menu,0,5,"Stop work and exit");
   GetCursorPos(out var pt);SetForegroundWindow(hwnd);var result=TrackPopupMenu(menu,0x100|0x2,pt.X,pt.Y,0,hwnd,0);
   switch(result){case 1:ShowWorkspace();break;case 2:ShowWorkspace();_=capture.OpenAsync();break;case 3:ShowHistory();break;case 4:keepRunning=iconAdded&&!keepRunning;SavePreferences();break;case 5:StopExit();break;}
  }finally{DestroyMenu(menu);}}
 public void ShowHistory()
 {try{if(historyWindow!=null){historyWindow.Activate();return;}
  var history=new Window{Title="Notification history"};history.AppWindow.Resize(new Windows.Graphics.SizeInt32(600,500));
  var panel=new StackPanel{Spacing=8,Padding=new Thickness(16)};var heading=new TextBlock{Text="Notification history",FontSize=22};AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level1);panel.Children.Add(heading);
  var enabled=new CheckBox{Content="Show desktop notifications",IsChecked=center.Enabled};AutomationProperties.SetName(enabled,"Show desktop notifications");enabled.Checked+=(_,_)=>TryHistory(()=>center.ConfigureEnabled(true));enabled.Unchecked+=(_,_)=>TryHistory(()=>center.ConfigureEnabled(false));panel.Children.Add(enabled);
  var quiet=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
  var from=new TextBox{Text=center.QuietFrom.ToString("HH:mm"),Width=80,PlaceholderText="HH:mm"};AutomationProperties.SetName(from,"Quiet hours start");
  var until=new TextBox{Text=center.QuietUntil.ToString("HH:mm"),Width=80,PlaceholderText="HH:mm"};AutomationProperties.SetName(until,"Quiet hours end");
  var saveQuiet=new Button{Content="Save quiet hours"};AutomationProperties.SetName(saveQuiet,"Save quiet hours");
  var quietStatus=new TextBlock();saveQuiet.Click+=(_,_)=>{if(TimeOnly.TryParseExact(from.Text,"HH:mm",out var startTime)&&TimeOnly.TryParseExact(until.Text,"HH:mm",out var endTime)){quietStatus.Text=TryHistory(()=>center.ConfigureQuietHours(startTime,endTime))?"Quiet hours saved.":"Quiet hours could not be saved.";}else quietStatus.Text="Enter times as HH:mm.";};
  quiet.Children.Add(from);quiet.Children.Add(until);quiet.Children.Add(saveQuiet);panel.Children.Add(quiet);panel.Children.Add(quietStatus);
  var filter=new ComboBox{Header="Filter history",SelectedIndex=0};foreach(var label in new[]{"All","Needs attention","Task updates"})filter.Items.Add(label);AutomationProperties.SetName(filter,"Filter notification history");panel.Children.Add(filter);
  var list=new StackPanel{Spacing=6};panel.Children.Add(list);
  void Render(){list.Children.Clear();IReadOnlyList<LocalNotice> saved;try{saved=center.History;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){list.Children.Add(new TextBlock{Text="Notification history is unavailable."});return;}var notices=saved.Reverse().Where(n=>filter.SelectedIndex switch{1=>n.DecisionId!=null,2=>n.DecisionId==null,_=>true}).ToArray();if(notices.Length==0){list.Children.Add(new TextBlock{Text="No notifications in this filter."});return;}foreach(var notice in notices){var label=$"{notice.At.ToLocalTime():g} - {notice.Kind} - {notice.Title}";var button=new Button{Content=label,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left};AutomationProperties.SetName(button,label);button.Click+=(_,_)=>OpenNotice(notice);list.Children.Add(button);}}
  filter.SelectionChanged+=(_,_)=>Render();Render();history.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};history.Closed+=(_,_)=>historyWindow=null;historyWindow=history;history.Activate();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){window.ShowNotificationStatus("Notification history is unavailable.");}}
 bool TryHistory(Action action){try{action();return true;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){window.ShowNotificationStatus("Notification settings could not be saved.");return false;}}
 void OpenNotice(LocalNotice notice)
 {ShowWorkspace();try{if(!watcher.IsCurrent(notice)||NavigateNotice?.Invoke(notice)!=true)window.ShowNotificationStatus("This notification is no longer available in the current project.");}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){window.ShowNotificationStatus("This notification is no longer available in the current project.");}}
 public void Dispose()
 {if(disposed)return;disposed=true;if(ReferenceEquals(Current,this))Current=null;settingsTimer.Dispose();watcher.Dispose();historyWindow?.Close();
  if(iconAdded){var data=BaseIcon();Shell_NotifyIcon(NimDelete,ref data);iconAdded=false;}
  if(subclassAttached)RemoveWindowSubclass(hwnd,procedure,SubclassId);}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct NOTIFYICONDATA
 {public uint cbSize;public nint hWnd;public uint uID,uFlags,uCallbackMessage;public nint hIcon;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string szTip;public uint dwState,dwStateMask;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string szInfo;public uint uTimeoutOrVersion;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string szInfoTitle;public uint dwInfoFlags;public Guid guidItem;public nint hBalloonIcon;
  public uint uVersion{set=>uTimeoutOrVersion=value;}}
 [StructLayout(LayoutKind.Sequential)]struct POINT{public int X,Y;}
 [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate nint SubclassProc(nint hwnd,uint message,nuint wparam,nint lparam,nuint id,nuint data);
 [DllImport("comctl32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool SetWindowSubclass(nint hwnd,SubclassProc callback,nuint id,nuint data);
 [DllImport("comctl32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool RemoveWindowSubclass(nint hwnd,SubclassProc callback,nuint id);
 [DllImport("comctl32.dll")]static extern nint DefSubclassProc(nint hwnd,uint message,nuint wp,nint lp);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool Shell_NotifyIcon(uint message,ref NOTIFYICONDATA data);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
 [DllImport("user32.dll")]static extern nint LoadIcon(nint instance,nint name);
 [DllImport("user32.dll")]static extern nint CreatePopupMenu();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool AppendMenu(nint menu,uint flags,nuint id,string? text);
 [DllImport("user32.dll")]static extern uint TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint hwnd,nint rect);
 [DllImport("user32.dll")]static extern bool DestroyMenu(nint menu);
 [DllImport("user32.dll")]static extern bool GetCursorPos(out POINT point);
 [DllImport("user32.dll")]static extern bool SetForegroundWindow(nint hwnd);
}






