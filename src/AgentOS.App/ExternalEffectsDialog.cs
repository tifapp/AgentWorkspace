using AgentOS.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace AgentOS.App;
public sealed class ExternalEffectsDialog : Window
{
 readonly ProjectRuntime runtime;
 readonly ComboBox work=new(){Header="Completed work"},provider=new(){Header="Provider"},operation=new(){Header="Operation"};
 readonly TextBox origin=new(){Header="GitHub HTTPS API origin",Text="https://api.github.com"},owner=new(){Header="Owner"},repo=new(){Header="Repository"},branch=new(){Header="Branch"},baseBranch=new(){Header="PR base branch",Text="main"};
 readonly TextBox host=new(){Header="PostgreSQL host"},port=new(){Header="Port",Text="5432"},database=new(){Header="Database"},schema=new(){Header="Schema",Text="public"},user=new(){Header="User"};
 readonly TextBox backend=new(){Header="Deployment backend"},destination=new(){Header="Destination"},artifactPath=new(){Header="Tracked artifact path"},command=new(){Header="Command",AcceptsReturn=true},environment=new(){Header="Environment name"};
 readonly TextBox source=new(){Header="Tracked migration SQL path"},title=new(){Header="PR title"},body=new(){Header="PR body",AcceptsReturn=true};
 readonly TextBox review=new(){Header="Exact review",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=180},approver=new(){Header="Approver name"};
 readonly ComboBox history=new(){Header="Effect history"};readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap};
 readonly Button approve=new(){Content="Approve exact scope",IsEnabled=false},execute=new(){Content="Execute approved",IsEnabled=false};EffectIntent? selected;
 ExternalEffectsDialog(ProjectRuntime runtime)
 {
  this.runtime=runtime;Title="External effects";AppWindow.Resize(new Windows.Graphics.SizeInt32(760,850));
  var panel=new StackPanel{Spacing=7,Padding=new Thickness(14)};
  panel.Children.Add(new TextBlock{Text="Preparing and opening this window never executes an external effect. Save destination settings, choose completed work, then review, approve and execute separately.",TextWrapping=TextWrapping.Wrap});
  foreach(var w in runtime.Snapshot.Work.Where(x=>x.Status==WorkStatus.Completed))work.Items.Add(new ComboBoxItem{Content=w.ShortTask+"  "+w.Id[..8],Tag=w.Id});
  foreach(var v in new[]{"github","postgresql","deployment"})provider.Items.Add(v);
  foreach(var c in new Control[]{work,provider,operation,origin,owner,repo,branch,baseBranch,host,port,database,schema,user,backend,destination,artifactPath,command,environment,source,title,body,review,approver,history})panel.Children.Add(c);
  provider.SelectionChanged+=(_,_)=>{operation.Items.Clear();foreach(var v in (provider.SelectedItem as string) switch{"github"=>new[]{"branch","pull_request"},"postgresql"=>new[]{"migration"},"deployment"=>new[]{"deploy"},_=>Array.Empty<string>()})operation.Items.Add(v);operation.SelectedIndex=operation.Items.Count>0?0:-1;};
  Button save=Button("Save provider settings",SaveSettings),prepare=Button("Prepare exact scope",async()=>await Prepare()),inspect=Button("Inspect history",Inspect),cancel=Button("Cancel prepared",async()=>await Act(x=>runtime.CancelExternalEffectAsync(x))),reconcile=Button("Reconcile unknown",async()=>await Act(x=>runtime.ReconcileExternalEffectAsync(x)));
  approve.Click+=async(_,_)=>await Act(x=>runtime.ApproveExternalEffectAsync(x,selected!.Scope.Digest,approver.Text.Trim()));
  execute.Click+=async(_,_)=>await Act(x=>runtime.ExecuteExternalEffectAsync(x));
  foreach(var c in new UIElement[]{save,prepare,approve,execute,inspect,cancel,reconcile,status})panel.Children.Add(c);
  Content=new ScrollViewer{Content=panel};LoadSettings();Refresh();
 }
 static Button Button(string text,Action action){var b=new Button{Content=text};b.Click+=(_,_)=>action();return b;}
 static Button Button(string text,Func<Task> action){var b=new Button{Content=text};b.Click+=async(_,_)=>await action();return b;}
 void LoadSettings(){var s=runtime.ExternalSettings;if(s.GitHub is { } g){origin.Text=g.ApiOrigin;owner.Text=g.Owner;repo.Text=g.Repository;branch.Text=g.Branch;baseBranch.Text=g.BaseBranch;}if(s.PostgreSql is { } p){host.Text=p.Host;port.Text=p.Port.ToString();database.Text=p.Database;schema.Text=p.Schema;user.Text=p.User;}if(s.Deployment is { } d){backend.Text=d.Backend;destination.Text=d.Destination;artifactPath.Text=d.ArtifactPath;command.Text=d.Command;environment.Text=d.EnvironmentName;}status.Text="Provider availability: GitHub requires a persisted Windows Credential Manager target AgentOS/GitHub/<API authority>/<owner>/<repository>. PostgreSQL: "+PostgreSqlProvider.Discover().Status+" Deployment requires a verified isolated VM executor; none is installed by this dialog.";}
 void SaveSettings(){try{var old=runtime.ExternalSettings;var p=provider.SelectedItem as string;runtime.ConfigureExternalEffects(new(p=="github"?new(origin.Text.Trim(),owner.Text.Trim(),repo.Text.Trim(),branch.Text.Trim(),baseBranch.Text.Trim()):old.GitHub,p=="postgresql"?new(host.Text.Trim(),int.Parse(port.Text),database.Text.Trim(),schema.Text.Trim(),user.Text.Trim()):old.PostgreSql,p=="deployment"?new(backend.Text.Trim(),destination.Text.Trim(),artifactPath.Text.Trim(),command.Text,environment.Text.Trim()):old.Deployment));status.Text="Provider settings saved without credentials.";}catch(Exception ex){status.Text=ex.Message;}}
 async Task Prepare(){try{if(work.SelectedItem is not ComboBoxItem item||provider.SelectedItem is not string p||operation.SelectedItem is not string o)throw new InvalidOperationException("Select completed work, provider and operation.");selected=await runtime.PrepareExternalEffectAsync(new((string)item.Tag,p,o,p=="postgresql"?source.Text.Trim():null,p=="github"&&o=="pull_request"?title.Text:null,p=="github"&&o=="pull_request"?body.Text:null));Show(selected);Refresh();}catch(Exception ex){status.Text=ex.Message;}}
 void Show(EffectIntent intent){selected=intent;review.Text=$"State: {intent.State}\nCandidate: {intent.Scope.CandidateCommit}\nEvidence SHA-256: {intent.Scope.EvidenceSha256}\nArtifact SHA-256: {intent.Scope.ArtifactSha256}\nSQL/command SHA-256: {intent.Scope.CommandSha256}\nEnvironment SHA-256: {intent.Scope.EnvironmentSha256}\nDestination: {intent.Scope.Destination}\nOperation: {intent.Scope.Operation}\nParameters: {intent.Scope.ParametersJson}\nScope digest: {intent.Scope.Digest}\nConsequence: this operation may create or change the configured external resource. Unknown outcomes require inspection and explicit reconciliation.\n{intent.Detail}";approve.IsEnabled=intent.State==ExternalEffectState.Prepared;execute.IsEnabled=intent.State==ExternalEffectState.Approved;}
 void Refresh(){history.Items.Clear();foreach(var x in runtime.ListExternalEffects())history.Items.Add(new ComboBoxItem{Content=x.State+"  "+x.Scope.Kind+"  "+x.Id[..12],Tag=x.Id});}
 void Inspect(){if(history.SelectedItem is ComboBoxItem item&&runtime.InspectExternalEffect((string)item.Tag) is { } x)Show(x);}
 async Task Act(Func<string,Task<EffectIntent>> action){try{if(selected==null)throw new InvalidOperationException("Prepare or inspect an effect first.");Show(await action(selected.Id));Refresh();}catch(Exception ex){status.Text=ex.Message;}}
 public static Task OpenAsync(ProjectRuntime runtime){var window=new ExternalEffectsDialog(runtime);window.Activate();return Task.CompletedTask;}
}
