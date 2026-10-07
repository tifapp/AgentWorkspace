using AgentOS.Core;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
namespace AgentOS.Tests;
public static class ExternalEffectTests
{
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 static async Task Refuses(Func<Task> operation,string message){try{await operation();}catch(InvalidOperationException){return;}throw new Exception(message);}
 sealed class Credentials:IScopedCredentialProvider{public string? GetSecret(string target)=>target=="AgentOS/GitHub/api.github.com/acme/repo"?"fixture-token":null;}
 sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request);}
 static EffectScope Scope(string operation="pull_request",string? old=null)
 {
  var sha=new string('a',40);var parameters=JsonSerializer.Serialize(new GitHubEffect("https://api.github.com","acme","repo","agent/test",sha,old,"main","Reviewed change","Body"));
  return new("github",sha,new string('B',64),new string('C',64),new string('D',64),new string('E',64),"https://api.github.com/acme/repo",operation,parameters);
 }
 static HttpResponseMessage Reply(HttpStatusCode status,string json)=>new(status){Content=new StringContent(json,Encoding.UTF8,"application/json")};
 public static async Task RunAsync()
 {
  var root=Path.Combine(Path.GetTempPath(),"agentos-effects-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try
  {
   var journal=new EffectIntentJournal(Path.Combine(root,"approval"));var calls=0;
   var adapter=new GitHubEffects(journal,new Credentials(),new Handler(_=>{calls++;return Task.FromResult(Reply(HttpStatusCode.OK,"[]"));}));
   var prepared=await adapter.PrepareAsync("approval",Scope());
   await Refuses(()=>Task.FromResult(journal.Approve("approval",new string('F',64),"reviewer")),"Tampered approval digest accepted.");
   Check((await adapter.ExecuteAsync("approval")).State==ExternalEffectState.Prepared&&calls==0,"Unapproved effect sent a request.");
   journal.Approve("approval",prepared.Scope.Digest,"reviewer");
   await Refuses(()=>adapter.PrepareAsync("approval",Scope("branch")),"Changed evidence/scope reused an intent.");
   var bad=journal.Read("approval")!;journal.Save(bad with{Scope=bad.Scope with{EvidenceSha256=new string('F',64)}});
   Check((await adapter.ExecuteAsync("approval")).State==ExternalEffectState.Approved&&calls==0,"Tampered approval executed.");

   var prJournal=new EffectIntentJournal(Path.Combine(root,"lost-response"));var posts=0;var checks=0;
   var sha=new string('a',40);var branch="{\"object\":{\"sha\":\""+sha+"\"}}";
   var pr="[{\"head\":{\"sha\":\""+sha+"\"},\"state\":\"open\",\"title\":\"Reviewed change\",\"body\":\"Body\",\"number\":7}]";
   var handler=new Handler(req=>
   {
    if(req.RequestUri!.AbsolutePath.EndsWith("/pulls")&&req.Method==HttpMethod.Post){posts++;throw new IOException("response lost");}
    if(req.RequestUri.AbsolutePath.EndsWith("/pulls")){checks++;return Task.FromResult(Reply(HttpStatusCode.OK,posts==0?"[]":pr));}
    return Task.FromResult(Reply(HttpStatusCode.OK,branch));
   });
   var github=new GitHubEffects(prJournal,new Credentials(),handler);
   var intent=await github.PrepareAsync("lost",Scope());prJournal.Approve("lost",intent.Scope.Digest,"reviewer");
   Check((await github.ExecuteAsync("lost")).State==ExternalEffectState.Unknown&&posts==1,"Lost response should remain unknown.");
   Check((await github.ReconcileAsync("lost")).State==ExternalEffectState.Completed&&posts==1&&checks>=2,"Reconciliation duplicated PR creation.");
   Check((await github.ExecuteAsync("lost")).State==ExternalEffectState.Completed&&posts==1,"Completed PR replayed.");

   var raceJournal=new EffectIntentJournal(Path.Combine(root,"race"));var racePosts=0;
   var race=new GitHubEffects(raceJournal,new Credentials(),new Handler(req=>{if(req.Method==HttpMethod.Post)racePosts++;return Task.FromResult(Reply(HttpStatusCode.OK,"{\"object\":{\"sha\":\""+new string('b',40)+"\"}}"));}));
   var raced=await race.PrepareAsync("race",Scope("branch",new string('c',40)));raceJournal.Approve("race",raced.Scope.Digest,"reviewer");
   Check((await race.ExecuteAsync("race")).State==ExternalEffectState.Stale&&racePosts==0,"Expected-old branch race was not refused.");

   var pgJournal=new EffectIntentJournal(Path.Combine(root,"postgres"));var sql="SELECT 1;";var sqlHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
   var pgScope=new EffectScope("postgresql",sha,new string('B',64),new string('C',64),sqlHash,new string('E',64),"database-identity","migration",JsonSerializer.Serialize(new PostgreSqlMigration("database-identity","public","001",sql,sqlHash)));
   var pg=new PostgreSqlEffects(pgJournal,null);var migration=await pg.PrepareAsync("pg",pgScope);pgJournal.Approve("pg",migration.Scope.Digest,"reviewer");
   Check((await pg.ExecuteAsync("pg")).State==ExternalEffectState.Unavailable,"Missing PostgreSQL provider did not fail closed.");
   var deployJournal=new EffectIntentJournal(Path.Combine(root,"deploy"));var deployScope=new EffectScope("deployment",sha,new string('B',64),new string('C',64),new string('D',64),new string('E',64),"prod","deploy",JsonSerializer.Serialize(new DeploymentEffect("vm",new string('C',64),new string('D',64),new string('E',64),"prod")));
   var deploy=new DeploymentEffects(deployJournal,null);var deployment=await deploy.PrepareAsync("deploy",deployScope);deployJournal.Approve("deploy",deployment.Scope.Digest,"reviewer");
   Check((await deploy.ExecuteAsync("deploy")).State==ExternalEffectState.Unavailable,"Missing isolated executor did not fail closed.");
  }
  finally{Directory.Delete(root,true);}
 }
}
