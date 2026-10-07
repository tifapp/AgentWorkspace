using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
public sealed record PostgreSqlEndpoint(string Host,int Port,string Database,string User,string TrustedRootCertificate,string TrustedRootSha256);
public sealed record PostgreSqlMigration(string DatabaseIdentity,string Schema,string MigrationId,string Sql,string SqlSha256,string? Role=null,bool SchemaRestrictedRole=false,PostgreSqlEndpoint? Endpoint=null);
public static class PostgreSqlProvider
{
    public static string EndpointIdentity(string host,int port,string database)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant()+":"+port+"\n"+database)));
    public static (DbProviderFactory? Factory,string Status) Discover()=>(Npgsql.NpgsqlFactory.Instance,"Npgsql 10.0.3 available.");
    public static Func<DbConnection> ConnectionFactory(DbProviderFactory factory,string connectionString)=>()=>{var c=factory.CreateConnection()??throw new InvalidOperationException("Provider did not create connection.");c.ConnectionString=connectionString;return c;};
    // Dedicated login: CREATE/USAGE only on approved existing schema; own agentos_migrations;
    // no superuser, role admin, database CREATE, other-schema CREATE, or server-file/program rights.
    // Store password as persistent generic Windows credential at the exact target below.
    public static Func<DbConnection> SecureConnectionFactory(PostgreSqlEffectSettings settings,IScopedCredentialProvider credentials)
    {
      if(!settings.SchemaRestrictedRole||string.IsNullOrWhiteSpace(settings.TrustedRootCertificate)||!Path.IsPathFullyQualified(settings.TrustedRootCertificate)||!File.Exists(settings.TrustedRootCertificate))throw new InvalidOperationException("Explicit TLS root and restricted role required.");
      var target="AgentOS/PostgreSQL/"+settings.Host.ToLowerInvariant()+"/"+settings.Port+"/"+settings.Database+"/"+settings.User;
      return ()=>{var secret=credentials.GetSecret(target);if(string.IsNullOrEmpty(secret))throw new InvalidOperationException("Scoped PostgreSQL credential unavailable.");
        var b=new Npgsql.NpgsqlConnectionStringBuilder{Host=settings.Host,Port=settings.Port,Database=settings.Database,Username=settings.User,Password=secret,SslMode=Npgsql.SslMode.VerifyFull,RootCertificate=settings.TrustedRootCertificate,Pooling=false,Timeout=15,CommandTimeout=30};
        return new Npgsql.NpgsqlConnection(b.ConnectionString);};
    }
}public sealed class PostgreSqlEffects : EffectAdapterBase
{
    private readonly Func<DbConnection>? connections;
    public string DependencyStatus { get; }
    public PostgreSqlEffects(EffectIntentJournal journal, Func<DbConnection>? connections,string? unavailableReason=null) : base(journal, "postgresql")
    {
        this.connections = connections;
        DependencyStatus = connections == null ? unavailableReason ?? "PostgreSQL connection provider unavailable." : "Connection provider configured; server capability unverified until connection opens.";
    }
    private static string Quoted(string value)
    {
        if (!Regex.IsMatch(value, "^[a-z_][a-z0-9_]{0,62}$")) throw new InvalidDataException("Unsafe PostgreSQL identifier.");
        return "\"" + value + "\"";
    }
    private static PostgreSqlMigration Spec(EffectIntent value)
    {
        var s = JsonSerializer.Deserialize<PostgreSqlMigration>(value.Scope.ParametersJson) ?? throw new InvalidDataException("Migration parameters missing.");
        if (string.IsNullOrWhiteSpace(s.DatabaseIdentity) || string.IsNullOrWhiteSpace(s.MigrationId) || s.MigrationId.Length > 200 ||
            s.DatabaseIdentity != value.Scope.Destination || value.Scope.Operation != "migration" ||
            s.SqlSha256 != value.Scope.CommandSha256 || !s.SchemaRestrictedRole || string.IsNullOrWhiteSpace(s.Role) ||
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Sql))) != s.SqlSha256) throw new InvalidDataException("Migration scope mismatch.");
        _ = Quoted(s.Schema); PostgreSqlMigrationGuard.Validate(s.Sql,s.Schema); return s;
    }
    private static string Identity(DbConnection connection)
    {
        if(connection is Npgsql.NpgsqlConnection pg){var b=new Npgsql.NpgsqlConnectionStringBuilder(pg.ConnectionString);var host=b.Host;if(string.IsNullOrWhiteSpace(host))throw new InvalidDataException("PostgreSQL endpoint host missing.");return PostgreSqlProvider.EndpointIdentity(host,b.Port,pg.Database);}
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.DataSource+"\n"+connection.Database)));
    }
    private static async Task<int> Execute(DbConnection c, DbTransaction tx, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.CommandTimeout = 30;
        foreach (var (name, data) in args) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = data; cmd.Parameters.Add(p); }
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<string?> Scalar(DbConnection c, DbTransaction? tx, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.CommandTimeout = 30;
        foreach (var (name, data) in args) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = data; cmd.Parameters.Add(p); }
        var result = await cmd.ExecuteScalarAsync(ct); return result is null or DBNull ? null : Convert.ToString(result);
    }
    protected override async Task<EffectOutcome> ApplyAsync(EffectIntent value, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); ct = timeout.Token;
        if (connections == null) return new(ExternalEffectState.Unavailable, DependencyStatus);
        var s = Spec(value);
        await using var c = connections(); await c.OpenAsync(ct);
        if (Identity(c) != s.DatabaseIdentity) return new(ExternalEffectState.Stale, "Connected database identity differs from approved scope.");
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var role=await Scalar(c,tx,@"SELECT CASE WHEN current_user=@role
                AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls AND NOT r.rolreplication
                AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                AND NOT pg_has_role(current_user,'pg_execute_server_program','MEMBER')
                AND NOT pg_has_role(current_user,'pg_read_server_files','MEMBER')
                AND NOT pg_has_role(current_user,'pg_write_server_files','MEMBER')
                AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT IN ('pg_catalog','information_schema','pg_toast',@schema)
                    AND (has_schema_privilege(current_user,n.oid,'CREATE') OR has_schema_privilege(current_user,n.oid,'USAGE')))
                AND has_schema_privilege(current_user,@schema,'CREATE')
                AND has_schema_privilege(current_user,@schema,'USAGE') THEN 'ok' END
                FROM pg_roles r WHERE r.rolname=current_user",ct,("role",s.Role!),("schema",s.Schema));
            if(role!="ok"){await tx.RollbackAsync(ct);return new(ExternalEffectState.Stale,"Database role lacks approved schema restriction.");}
            // Both declared resources stay locked for the transaction containing the migration and record.
            await Scalar(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@resource, 0))", ct, ("resource", "database:" + s.DatabaseIdentity));
            await Scalar(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@resource, 0))", ct, ("resource", "schema:" + s.DatabaseIdentity + ":" + s.Schema));
            var schema = Quoted(s.Schema);

            await Execute(c, tx, "CREATE TABLE IF NOT EXISTS " + schema + ".agentos_migrations (migration_id text PRIMARY KEY, sql_sha256 text NOT NULL, database_identity text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
            var ledger=await Scalar(c,tx,@"SELECT CASE WHEN c.relowner=(SELECT oid FROM pg_roles WHERE rolname=current_user)
                AND c.relkind='r' AND NOT c.relhastriggers AND NOT c.relhasrules THEN 'ok' END
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=@schema AND c.relname='agentos_migrations'",ct,("schema",s.Schema));
            if(ledger!="ok"){await tx.RollbackAsync(ct);return new(ExternalEffectState.Stale,"Migration ledger ownership or type differs.");}
            var existing = await Scalar(c, tx, "SELECT sql_sha256 FROM " + schema + ".agentos_migrations WHERE migration_id=@id", ct, ("id", s.MigrationId));
            if (existing != null)
            {
                await tx.RollbackAsync(ct);
                return existing == s.SqlSha256 ? new(ExternalEffectState.Completed, "Exact migration record already exists.", s.MigrationId) : new(ExternalEffectState.Stale, "Migration ID has another SQL hash.");
            }
            await Execute(c, tx, s.Sql, ct);
            await Execute(c, tx, "INSERT INTO " + schema + ".agentos_migrations (migration_id,sql_sha256,database_identity) VALUES (@id,@hash,@database)", ct,
                ("id", s.MigrationId), ("hash", s.SqlSha256), ("database", s.DatabaseIdentity));
            await tx.CommitAsync(ct);
            return new(ExternalEffectState.Completed, "Migration and durable record committed together.", s.MigrationId);
        }
        catch { try { await tx.RollbackAsync(CancellationToken.None); } catch { } throw; }
    }
    protected override async Task<EffectOutcome> CheckAsync(EffectIntent value, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); ct = timeout.Token;
        if (connections == null) return new(ExternalEffectState.Unknown, DependencyStatus);
        var s = Spec(value);
        await using var c = connections(); await c.OpenAsync(ct);
        if (Identity(c) != s.DatabaseIdentity) return new(ExternalEffectState.Unknown, "Frozen database identity cannot be verified.");
        var schema = Quoted(s.Schema);
        var exists = await Scalar(c, null, "SELECT to_regclass(@name)", ct, ("name", s.Schema + ".agentos_migrations"));
        if (exists == null) return new(ExternalEffectState.Unknown, "No migration record; manual review required before retry.");
        var hash = await Scalar(c, null, "SELECT sql_sha256 FROM " + schema + ".agentos_migrations WHERE migration_id=@id", ct, ("id", s.MigrationId));
        if (hash == s.SqlSha256) return new(ExternalEffectState.Completed, "Exact committed migration record confirmed.", s.MigrationId);
        if (hash != null) return new(ExternalEffectState.Unknown, "Historical migration record differs.");
        return new(ExternalEffectState.Unknown, "Migration record absent; no automatic replay.");
    }
}







