using AgentOS.Core;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Tests;
public static partial class ExternalDeliveryChecks
{
 static async Task DatabaseDeliveryAsync()
 {
  var state=new FakeDatabase();var sql="CREATE TABLE approved.items (id integer DEFAULT 'COMMIT');";state.Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
  var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture:5432\nfixture")));
  Check(PostgreSqlProvider.EndpointIdentity("FIXTURE",5432,"fixture")==identity,"Canonical endpoint identity changed");
  var spec=new PostgreSqlMigration(identity,"approved","001",sql,state.Hash,"fixture_role",true);
  var scope=new EffectScope("postgresql",new string('a',40),new string('B',64),new string('C',64),state.Hash,new string('E',64),identity,"migration",JsonSerializer.Serialize(spec));
  var root=Path.Combine(Path.GetTempPath(),"agentos-db-delivery-"+Guid.NewGuid().ToString("N"));
  try{var journal=new EffectIntentJournal(root);var adapter=new PostgreSqlEffects(journal,()=>new FakeConnection(state));var intent=await adapter.PrepareAsync("db",scope);
   Check((await adapter.ExecuteAsync("db")).State==ExternalEffectState.Prepared&&state.Commands.Count==0,"Unapproved SQL ran.");journal.Approve("db",intent.Scope.Digest,"reviewer");
   Check((await adapter.ExecuteAsync("db")).State==ExternalEffectState.Unknown&&state.MigrationRuns==1,"Lost commit acknowledgment hidden.");
   Check(state.Commands.All(x=>x.Transaction!=null),"Database command escaped migration transaction.");
   Check((await adapter.ReconcileAsync("db")).State==ExternalEffectState.Completed&&state.MigrationRuns==1,"Reconciliation replayed migration.");
   await HistoricalRuntimeReconcileAsync(scope,state);
  }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 static async Task HistoricalRuntimeReconcileAsync(EffectScope scope,FakeDatabase state)
 {
  var root=Path.Combine(Path.GetTempPath(),"agentos-historical-"+Guid.NewGuid().ToString("N"));
  try{Directory.CreateDirectory(root);var project=await PracticeProject.CreateAsync(root);var stateRoot=Path.Combine(root,"state");await using var runtime=await ProjectRuntime.OpenInternal(project,stateRoot,new ScriptHost());
   var cert=Path.Combine(root,"root.pem");await File.WriteAllTextAsync(cert,"fixture root");runtime.ConfigureExternalEffects(new ExternalEffectSettings(PostgreSql:new PostgreSqlEffectSettings("fixture",5432,"fixture","approved","fixture_role",cert,true)));
   var journal=new EffectIntentJournal(Path.Combine(runtime.DataDirectory,"external-effects"));var adapter=new PostgreSqlEffects(journal,()=>new FakeConnection(state));var intent=await adapter.PrepareAsync("historical",scope);journal.Approve("historical",intent.Scope.Digest,"reviewer");journal.Save(journal.Read("historical")! with{State=ExternalEffectState.Unknown});
   runtime.ConfigureExternalEffects(new ExternalEffectSettings(PostgreSql:new PostgreSqlEffectSettings("changed",5432,"fixture","approved","fixture_role",cert,true)));
   Check((await runtime.ReconcileExternalEffectAsync("historical",connection:()=>new FakeConnection(state))).State==ExternalEffectState.Completed,"Settings drift blocked historical receipt lookup.");
   var count=state.Commands.Count;try{await runtime.ExecuteExternalEffectAsync("historical",connection:()=>new FakeConnection(state));throw new Exception("Stale execution accepted");}catch(InvalidOperationException){}Check(state.Commands.Count==count,"Stale execution queried database");
  }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 sealed class FakeDatabase{internal readonly List<(string Sql,DbTransaction? Transaction)> Commands=new();internal int MigrationRuns;internal bool Durable;internal bool LoseCommit=true;internal string Hash="";}
 sealed class FakeConnection(FakeDatabase state):DbConnection
 {
  ConnectionState current=ConnectionState.Closed;
  public override string ConnectionString{get;set;}="";public override string Database=>"fixture";public override string DataSource=>"fixture:5432";public override string ServerVersion=>"16";public override ConnectionState State=>current;
  public override void ChangeDatabase(string name)=>throw new NotSupportedException();public override void Close()=>current=ConnectionState.Closed;public override void Open()=>current=ConnectionState.Open;
  public override Task OpenAsync(CancellationToken ct){Open();return Task.CompletedTask;}
  protected override DbTransaction BeginDbTransaction(IsolationLevel level)=>new FakeTransaction(this,state);
  protected override DbCommand CreateDbCommand()=>new FakeCommand(this,state);
 }
 sealed class FakeTransaction(FakeConnection connection,FakeDatabase state):DbTransaction
 {
  public override IsolationLevel IsolationLevel=>IsolationLevel.Serializable;protected override DbConnection DbConnection=>connection;
  public override void Commit(){state.Durable=true;if(state.LoseCommit){state.LoseCommit=false;throw new IOException("commit acknowledgment lost");}}
  public override Task CommitAsync(CancellationToken ct=default){Commit();return Task.CompletedTask;}
  public override void Rollback(){}public override Task RollbackAsync(CancellationToken ct=default)=>Task.CompletedTask;
 }
 sealed class FakeCommand(FakeConnection connection,FakeDatabase state):DbCommand
 {
  readonly FakeParameters parameters=new();public override string CommandText{get;set;}="";public override int CommandTimeout{get;set;}
  public override CommandType CommandType{get;set;}=CommandType.Text;public override bool DesignTimeVisible{get;set;}public override UpdateRowSource UpdatedRowSource{get;set;}
  protected override DbConnection DbConnection{get=>connection;set=>throw new NotSupportedException();}protected override DbTransaction? DbTransaction{get;set;}protected override DbParameterCollection DbParameterCollection=>parameters;
  public override void Cancel(){}public override void Prepare(){}protected override DbParameter CreateDbParameter()=>new FakeParameter();protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)=>throw new NotSupportedException();
  public override int ExecuteNonQuery(){state.Commands.Add((CommandText,DbTransaction));if(CommandText.StartsWith("CREATE TABLE approved.items",StringComparison.Ordinal))state.MigrationRuns++;return 1;}
  public override Task<int> ExecuteNonQueryAsync(CancellationToken ct)=>Task.FromResult(ExecuteNonQuery());
  public override object? ExecuteScalar(){state.Commands.Add((CommandText,DbTransaction));if(CommandText.Contains("FROM pg_roles",StringComparison.Ordinal)||CommandText.Contains("FROM pg_class",StringComparison.Ordinal))return "ok";
   if(CommandText.Contains("to_regclass",StringComparison.Ordinal))return state.Durable?"approved.agentos_migrations":null;
   if(CommandText.Contains("SELECT sql_sha256",StringComparison.Ordinal))return state.Durable?state.Hash:null;return null;}
  public override Task<object?> ExecuteScalarAsync(CancellationToken ct)=>Task.FromResult(ExecuteScalar());
 }
 sealed class FakeParameters:DbParameterCollection
 {
  readonly List<DbParameter> list=new();public override int Count=>list.Count;public override object SyncRoot=>this;
  public override int Add(object value){list.Add((DbParameter)value);return list.Count-1;}public override void AddRange(Array values){foreach(var value in values)Add(value!);}
  public override void Clear()=>list.Clear();public override bool Contains(object value)=>list.Contains((DbParameter)value);public override bool Contains(string value)=>list.Any(x=>x.ParameterName==value);
  public override void CopyTo(Array array,int index)=>((ICollection)list).CopyTo(array,index);public override IEnumerator GetEnumerator()=>list.GetEnumerator();
  public override int IndexOf(object value)=>list.IndexOf((DbParameter)value);public override int IndexOf(string name)=>list.FindIndex(x=>x.ParameterName==name);
  public override void Insert(int index,object value)=>list.Insert(index,(DbParameter)value);public override void Remove(object value)=>list.Remove((DbParameter)value);
  public override void RemoveAt(int index)=>list.RemoveAt(index);public override void RemoveAt(string name){var i=IndexOf(name);if(i>=0)list.RemoveAt(i);}
  protected override DbParameter GetParameter(int index)=>list[index];protected override DbParameter GetParameter(string name)=>list[IndexOf(name)];
  protected override void SetParameter(int index,DbParameter value)=>list[index]=value;protected override void SetParameter(string name,DbParameter value){var i=IndexOf(name);if(i<0)list.Add(value);else list[i]=value;}
 }
 sealed class FakeParameter:DbParameter
 {
  public override DbType DbType{get;set;}public override ParameterDirection Direction{get;set;}=ParameterDirection.Input;public override bool IsNullable{get;set;}
  public override string ParameterName{get;set;}="";public override string SourceColumn{get;set;}="";public override object? Value{get;set;}
  public override DataRowVersion SourceVersion{get;set;}=DataRowVersion.Current;public override bool SourceColumnNullMapping{get;set;}public override int Size{get;set;}
  public override void ResetDbType(){}
 }
}




