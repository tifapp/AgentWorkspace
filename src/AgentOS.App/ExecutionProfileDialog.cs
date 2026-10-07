using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Security.Cryptography;
namespace AgentOS.App;
internal static class ExecutionProfileDialog
{
 internal static async Task ShowAsync(XamlRoot root,string dataRoot)
 {
  var registry=new ExecutionProfileRegistry(dataRoot);var current=registry.Current;var backend=new ComboBox{Header="Execution backend",HorizontalAlignment=HorizontalAlignment.Stretch};
  backend.Items.Add("AppContainer (default)");backend.Items.Add("Hyper-V SDK guest (offline)");backend.Items.Add(new ComboBoxItem{Content="Windows Sandbox (unavailable: shutdown proof missing)",IsEnabled=false});backend.SelectedIndex=current.SdkBackend switch{ExecutionBackend.HyperV=>1,ExecutionBackend.WindowsSandbox=>2,_=>0};
  var image=new TextBox{Header="Base VHD path",Text=current.HyperV?.BaseVhdPath??""};var hash=new TextBox{Header="Base image SHA-256",Text=current.HyperV?.BaseVhdSha256??""};
  var guest=new TextBox{Header="Guest administrator credential target",Text=current.HyperV?.GuestCredentialTarget??""};var worker=new TextBox{Header="Separate nonadministrator SDK worker credential target",Text=current.HyperV?.WorkerCredentialTarget??""};
  var destination=new TextBox{Header="Destination credential target",Text=current.HyperV?.DestinationCredentialTarget??""};var vmSwitch=new TextBox{Header="Hyper-V switch (empty keeps VM offline)",Text=current.HyperV?.SwitchName??""};var ips=new TextBox{Header="Pinned destination IPs (comma separated)",Text=string.Join(", ",current.HyperV?.PinnedDestinationIps??[])};
  var body=new StackPanel{Spacing=8};foreach(var item in new UIElement[]{backend,new TextBlock{Text="Availability: "+registry.Describe().Reason,TextWrapping=TextWrapping.Wrap},image,hash,guest,worker,destination,vmSwitch,ips,new TextBlock{Text="Hyper-V is opt-in for new work. Saving does not enable Windows features, provision an image, or copy credentials.",TextWrapping=TextWrapping.Wrap}})body.Children.Add(item);
  var dialog=new ContentDialog{Title="Native SDK execution profile",Content=new ScrollViewer{Content=body,MaxHeight=500},PrimaryButtonText="Save profile",CloseButtonText="Cancel",XamlRoot=root};if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
  if(backend.SelectedIndex==2)throw new NotSupportedException("Windows Sandbox SDK execution unavailable.");HyperVProfile? profile=null;
  if(backend.SelectedIndex==1){if(!File.Exists(image.Text))throw new FileNotFoundException("Base VHD unavailable.",image.Text);await using var stream=File.OpenRead(image.Text);var actual=Convert.ToHexString(await SHA256.HashDataAsync(stream));if(!actual.Equals(hash.Text.Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Base image SHA-256 mismatch.");profile=new HyperVProfile(image.Text,hash.Text.Trim(),guest.Text.Trim(),vmSwitch.Text.Trim(),string.IsNullOrWhiteSpace(destination.Text)?null:destination.Text.Trim(),ips.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries),worker.Text.Trim());}
  registry.Configure(new ExecutionProfile(backend.SelectedIndex==1?ExecutionBackend.HyperV:ExecutionBackend.AppContainer,profile));
 }
 internal static async Task ReconcileAsync(XamlRoot root,ProjectRuntime runtime)
 {
  var pending=runtime.Snapshot.Work.Select(w=>(Work:w,Operation:runtime.PendingSdkOperationId(w.Id))).Where(x=>x.Operation!=null).ToArray();
  if(pending.Length==0){await new ContentDialog{Title="SDK VM reconciliation",Content="No persisted SDK VM operation needs reconciliation.",CloseButtonText="Close",XamlRoot=root}.ShowAsync();return;}
  var choice=new ComboBox{Header="Exact persisted SDK operation",HorizontalAlignment=HorizontalAlignment.Stretch};foreach(var item in pending)choice.Items.Add(item.Work.ShortTask+" · "+item.Work.Id+" / "+item.Operation);choice.SelectedIndex=0;
  var dialog=new ContentDialog{Title="Reconcile SDK VM",Content=choice,PrimaryButtonText="Query exact VM",CloseButtonText="Cancel",XamlRoot=root};if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
  var selected=pending[choice.SelectedIndex];var receipt=await runtime.ReconcileSdkAsync(selected.Work.Id,selected.Operation!);await new ContentDialog{Title="SDK reconciliation receipt",Content=new ScrollViewer{Content=new TextBlock{Text=System.Text.Json.JsonSerializer.Serialize(receipt),TextWrapping=TextWrapping.Wrap},MaxHeight=400},CloseButtonText="Close",XamlRoot=root}.ShowAsync();
 }
}
