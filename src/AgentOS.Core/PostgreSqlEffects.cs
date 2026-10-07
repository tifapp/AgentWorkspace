using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
public sealed record PostgreSqlMigration(string DatabaseIdentity, string Schema, string MigrationId, string Sql, string SqlSha256);
public static class PostgreSqlProvider
{
    public static (DbProviderFactory? Factory, string Status) Discover()
    {
        var type = Type.GetType("Npgsql.NpgsqlFactory, Npgsql", throwOnError: false);
        var property = type?.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        return property?.GetValue(null) is DbProviderFactory factory
            ? (factory, "Npgsql available.")
            : (null, "Npgsql provider unavailable. Install Npgsql in the host application before PostgreSQL effects can run.");
    }
    public static Func<DbConnection> ConnectionFactory(DbProviderFactory factory, string connectionString)
        => () => { var connection = factory.CreateConnection() ?? throw new InvalidOperationException("Provider did not create a connection."); connection.ConnectionString = connectionString; return connection; };
}
public sealed class PostgreSqlEffects : EffectAdapterBase
{
    private readonly Func<DbConnection>? connections;
    public string DependencyStatus { get; }
    public PostgreSqlEffects(EffectIntentJournal journal, Func<DbConnection>? connections) : base(journal, "postgresql")
    {
        this.connections = connections;
        DependencyStatus = connections == null ? "PostgreSQL connection provider unavailable." : "Connection provider configured; server capability unverified until connection opens.";
    }
    private static string Quoted(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]{0,62}$")) throw new InvalidDataException("Unsafe PostgreSQL identifier.");
        return "\"" + value + "\"";
    }
    private static PostgreSqlMigration Spec(EffectIntent value)
    {
        var s = JsonSerializer.Deserialize<PostgreSqlMigration>(value.Scope.ParametersJson) ?? throw new InvalidDataException("Migration parameters missing.");
        if (string.IsNullOrWhiteSpace(s.DatabaseIdentity) || string.IsNullOrWhiteSpace(s.MigrationId) || s.MigrationId.Length > 200 ||
            s.DatabaseIdentity != value.Scope.Destination || value.Scope.Operation != "migration" ||
            s.SqlSha256 != value.Scope.CommandSha256 ||
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Sql))) != s.SqlSha256) throw new InvalidDataException("Migration scope mismatch.");
        _ = Quoted(s.Schema); return s;
    }
    private static string Identity(DbConnection connection)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.DataSource + "\n" + connection.Database)));
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
        var s = Spec(value);
        if (connections == null) return new(ExternalEffectState.Unavailable, DependencyStatus);
        await using var c = connections(); await c.OpenAsync(ct);
        if (Identity(c) != s.DatabaseIdentity) return new(ExternalEffectState.Stale, "Connected database identity differs from approved scope.");
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // Both declared resources stay locked for the transaction containing the migration and record.
            await Scalar(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@resource, 0))", ct, ("resource", "database:" + s.DatabaseIdentity));
            await Scalar(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@resource, 0))", ct, ("resource", "schema:" + s.DatabaseIdentity + ":" + s.Schema));
            var schema = Quoted(s.Schema);
            // Schema creation is part of the approved migration transaction.
            await Execute(c, tx, "CREATE SCHEMA IF NOT EXISTS " + schema, ct);
            await Execute(c, tx, "CREATE TABLE IF NOT EXISTS " + schema + ".agentos_migrations (migration_id text PRIMARY KEY, sql_sha256 text NOT NULL, database_identity text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
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
        var s = Spec(value);
        if (connections == null) return new(ExternalEffectState.Unknown, DependencyStatus);
        await using var c = connections(); await c.OpenAsync(ct);
        if (Identity(c) != s.DatabaseIdentity) return new(ExternalEffectState.Stale, "Connected database changed.");
        var schema = Quoted(s.Schema);
        var exists = await Scalar(c, null, "SELECT to_regclass(@name)", ct, ("name", s.Schema + ".agentos_migrations"));
        if (exists == null) return new(ExternalEffectState.Unknown, "No migration record; manual review required before retry.");
        var hash = await Scalar(c, null, "SELECT sql_sha256 FROM " + schema + ".agentos_migrations WHERE migration_id=@id", ct, ("id", s.MigrationId));
        if (hash == s.SqlSha256) return new(ExternalEffectState.Completed, "Exact committed migration record confirmed.", s.MigrationId);
        if (hash != null) return new(ExternalEffectState.Stale, "Migration record has another SQL hash.");
        return new(ExternalEffectState.Unknown, "Migration record absent; no automatic replay.");
    }
}


