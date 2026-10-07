using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
 readonly System.Threading.Timer settingsTimer;string? selectedProject,selectedRoot;bool keepRunning,exiting,disposed,iconAdded;Window? historyWindow;LocalNotice? lastBalloon;
 public Func<LocalNotice,bool>? NavigateNotice {get;set;}
 public NotificationController(MainWindow window,CaptureController capture)
 {this.window=window;this.capture=capture;queue=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();hwnd=WindowNative.GetWindowHandle(window);
  center=new NotificationCenter();watcher=new ProjectNotificationWatcher(center,n=>queue.TryEnqueue(()=>Deliver(n)));
  procedure=WindowMessage;taskbarCreated=RegisterWindowMessage("TaskbarCreated");
  if(hwnd==0||!SetWindowSubclass(hwnd,procedure,SubclassId,0))throw new InvalidOperationException("Tray window subclass could not be attached.");
  AddIcon();if(!iconAdded){RemoveWindowSubclass(hwnd,procedure,SubclassId);watcher.Dispose();throw new InvalidOperationException("The Windows notification area icon could not be created.");}settingsTimer=new System.Threading.Timer(_=>queue.TryEnqueue(RefreshProject),null,0,1000);
  window.Closed+=(_,_)=>Dispose();
 }
 void RefreshProject()
 {if(disposed)return;try{var settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentOS","desktop.json");
   if(!File.Exists(settings))return;using var file=new FileStream(settings,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var doc=JsonDocument.Parse(file);
   var project=doc.RootElement.TryGetProperty("project",out var p)?p.GetString():null;var root=doc.RootElement.TryGetProperty("dataRoot",out var r)?r.GetString():null;
   if(string.Equals(selectedProject,project,StringComparison.OrdinalIgnoreCase)&&string.Equals(selectedRoot,root,StringComparison.OrdinalIgnoreCase))return;
   selectedProject=project;selectedRoot=root;watcher.Select(project,root);
  }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){}}
 void Deliver(LocalNotice notice)
 {if(disposed||!iconAdded)return;lastBalloon=notice;var data=BaseIcon();data.uFlags=NifInfo;data.szInfoTitle="Agent OS · "+notice.Kind;data.szInfo=notice.Title;data.dwInfoFlags=1;Shell_NotifyIcon(NimModify,ref data);}
 void AddIcon()
 {var data=BaseIcon();data.uFlags=NifMessage|NifIcon|NifTip;iconAdded=Shell_NotifyIcon(NimAdd,ref data);if(iconAdded){data.uVersion=4;Shell_NotifyIcon(NimSetVersion,ref data);}}
 NOTIFYICONDATA BaseIcon()=>new(){cbSize=(uint)Marshal.SizeOf<NOTIFYICONDATA>(),hWnd=hwnd,uID=IconId,uCallbackMessage=Callback,hIcon=LoadIcon(0,(nint)32512),szTip="Agent OS",szInfo="",szInfoTitle=""};
 nint WindowMessage(nint h,uint message,nuint wp,nint lp,nuint id,nuint data)
 {if(message==taskbarCreated){AddIcon();return 0;}
  if(message==WmClose&&keepRunning&&!exiting){queue.TryEnqueue(()=>window.AppWindow.Hide());return 0;}
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
   switch(result){case 1:ShowWorkspace();break;case 2:ShowWorkspace();_=capture.OpenAsync();break;case 3:ShowHistory();break;case 4:keepRunning=!keepRunning;break;case 5:StopExit();break;}
  }finally{DestroyMenu(menu);}}
 void ShowHistory()
 {if(historyWindow!=null){historyWindow.Activate();return;}
  var history=new Window{Title="Notification history"};history.AppWindow.Resize(new Windows.Graphics.SizeInt32(600,500));
  var panel=new StackPanel{Spacing=8,Padding=new Thickness(16)};var heading=new TextBlock{Text="Notification history",FontSize=22};AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level1);panel.Children.Add(heading);
  var notices=center.History.Reverse().ToArray();if(notices.Length==0)panel.Children.Add(new TextBlock{Text="No notifications yet."});
  foreach(var notice in notices){var label=$"{notice.At.ToLocalTime():g} · {notice.Kind} · {notice.Title}";
   var button=new Button{Content=label,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left};AutomationProperties.SetName(button,label);
   button.Click+=(_,_)=>OpenNotice(notice);panel.Children.Add(button);}
  history.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};history.Closed+=(_,_)=>historyWindow=null;historyWindow=history;history.Activate();}
 void OpenNotice(LocalNotice notice)
 {ShowWorkspace();if(NavigateNotice?.Invoke(notice)==true)return;
  if(notice.WorkId==null)return;var root=window.Content as DependencyObject;if(root==null)return;
  var row=Find(root,"TaskRow_"+notice.WorkId);if(row==null)return;
  var target=Find(row,notice.DecisionId!=null?"TaskDecision":"TaskDetails") as Button;
  if(target==null||target.Visibility!=Visibility.Visible)return;
  target.Focus(FocusState.Programmatic);new ButtonAutomationPeer(target).Invoke();}
 static DependencyObject? Find(DependencyObject root,string id)
 {if(AutomationProperties.GetAutomationId(root)==id)return root;
  for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=Find(VisualTreeHelper.GetChild(root,i),id);if(found!=null)return found;}return null;}
 public void Dispose()
 {if(disposed)return;disposed=true;settingsTimer.Dispose();watcher.Dispose();historyWindow?.Close();
  if(iconAdded){var data=BaseIcon();Shell_NotifyIcon(NimDelete,ref data);iconAdded=false;}
  if(hwnd!=0)RemoveWindowSubclass(hwnd,procedure,SubclassId);}
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


