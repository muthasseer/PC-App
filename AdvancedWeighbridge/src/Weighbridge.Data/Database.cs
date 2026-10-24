using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Weighbridge.Data;

public enum DatabaseProvider
{
    Sqlite = 0,
    PostgreSql = 1,
}

public sealed record DatabaseOptions(DatabaseProvider Provider, string ConnectionString)
{
    public static DatabaseOptions SqliteFile(string path) => new(DatabaseProvider.Sqlite, new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString());

    /// <summary>The SQLite file path, when the provider is SQLite.</summary>
    public string? SqlitePath => Provider == DatabaseProvider.Sqlite ? new SqliteConnectionStringBuilder(ConnectionString).DataSource : null;
}

/// <summary>The few SQL differences between SQLite and PostgreSQL used by the stores.</summary>
public sealed class SqlDialect(DatabaseProvider provider)
{
    public DatabaseProvider Provider { get; } = provider;
    public bool IsPostgres => Provider == DatabaseProvider.PostgreSql;

    /// <summary>Row lock suffix for read-modify-write sequences (SQLite transactions are already exclusive writers).</summary>
    public string ForUpdate => IsPostgres ? " FOR UPDATE" : string.Empty;

    /// <summary>Case-insensitive "contains" comparison that works on both engines.</summary>
    public string ContainsCi(string column, string parameter) => $"lower({column}) LIKE lower({parameter}) ESCAPE '\\'";

    /// <summary>"column is one of the values in the list parameter" (Dapper expands lists on SQLite and passes arrays to PostgreSQL).</summary>
    public string InList(string column, string parameter) => IsPostgres ? $"{column} = ANY({parameter})" : $"{column} IN {parameter}";

    public static string LikePattern(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}

/// <summary>Unit of work: a connection plus an optional transaction. Stores receive the session explicitly.</summary>
public sealed class DbSession : IAsyncDisposable, IDisposable
{
    internal DbSession(DbConnection connection, DbTransaction? transaction, SqlDialect dialect)
    {
        Connection = connection;
        Transaction = transaction;
        Dialect = dialect;
    }

    public DbConnection Connection { get; }
    public DbTransaction? Transaction { get; private set; }
    public SqlDialect Dialect { get; }

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default) =>
        Connection.QueryAsync<T>(new CommandDefinition(sql, param, Transaction, cancellationToken: ct));

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default) =>
        Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, param, Transaction, cancellationToken: ct));

    public Task<T> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default) =>
        Connection.QuerySingleAsync<T>(new CommandDefinition(sql, param, Transaction, cancellationToken: ct));

    public Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default) =>
        Connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, param, Transaction, cancellationToken: ct));

    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default) =>
        Connection.ExecuteAsync(new CommandDefinition(sql, param, Transaction, cancellationToken: ct));

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (Transaction is null) return;
        await Transaction.CommitAsync(ct).ConfigureAwait(false);
        await Transaction.DisposeAsync().ConfigureAwait(false);
        Transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null)
        {
            try { await Transaction.RollbackAsync().ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            await Transaction.DisposeAsync().ConfigureAwait(false);
        }
        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Transaction is not null)
        {
            try { Transaction.Rollback(); }
            catch (InvalidOperationException) { }
            Transaction.Dispose();
        }
        Connection.Dispose();
    }
}

public sealed class ConcurrencyException(string message) : Exception(message);

public sealed class Database
{
    private readonly NpgsqlDataSource? _npgsql;

    static Database()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
        SqlMapper.AddTypeHandler(new IsoDateTimeOffsetHandler());
        SqlMapper.RemoveTypeMap(typeof(Guid));
        SqlMapper.RemoveTypeMap(typeof(Guid?));
        SqlMapper.AddTypeHandler(new GuidTextHandler());
        SqlMapper.AddTypeHandler(new BoolHandler());
    }

    public Database(DatabaseOptions options)
    {
        Options = options;
        Dialect = new SqlDialect(options.Provider);
        if (options.Provider == DatabaseProvider.PostgreSql) _npgsql = NpgsqlDataSource.Create(options.ConnectionString);
    }

    public DatabaseOptions Options { get; }
    public SqlDialect Dialect { get; }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        if (_npgsql is not null) return await _npgsql.OpenConnectionAsync(ct).ConfigureAwait(false);

        var connection = new SqliteConnection(Options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        // WAL: readers never block the writer; FULL sync: a committed weighing survives power loss.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>A session without a transaction (reads).</summary>
    public async Task<DbSession> OpenAsync(CancellationToken ct = default) =>
        new(await OpenConnectionAsync(ct).ConfigureAwait(false), null, Dialect);

    /// <summary>A session with a write transaction. SQLite takes the write lock immediately (BEGIN IMMEDIATE).</summary>
    public async Task<DbSession> BeginAsync(CancellationToken ct = default)
    {
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        DbTransaction transaction = connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false)
            : await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        return new DbSession(connection, transaction, Dialect);
    }

    public async Task<T> InTransactionAsync<T>(Func<DbSession, Task<T>> work, CancellationToken ct = default)
    {
        await using var session = await BeginAsync(ct).ConfigureAwait(false);
        var result = await work(session).ConfigureAwait(false);
        await session.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<T> ReadAsync<T>(Func<DbSession, Task<T>> work, CancellationToken ct = default)
    {
        await using var session = await OpenAsync(ct).ConfigureAwait(false);
        return await work(session).ConfigureAwait(false);
    }
}

/// <summary>Timestamps are stored as sortable ISO-8601 UTC text on both engines.</summary>
public static class Iso
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string ToText(DateTimeOffset value) => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
    public static string? ToText(DateTimeOffset? value) => value is { } v ? ToText(v) : null;

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

internal sealed class IsoDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = Iso.ToText(value);
    }

    public override DateTimeOffset Parse(object value) => value switch
    {
        string s => Iso.Parse(s),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        DateTimeOffset dto => dto,
        _ => throw new DataException($"Cannot convert {value.GetType().Name} to DateTimeOffset."),
    };
}

internal sealed class GuidTextHandler : SqlMapper.TypeHandler<Guid>
{
    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString("D");
    }

    public override Guid Parse(object value) => value switch
    {
        string s => Guid.Parse(s),
        Guid g => g,
        byte[] b => new Guid(b),
        _ => throw new DataException($"Cannot convert {value.GetType().Name} to Guid."),
    };
}

/// <summary>SQLite returns 0/1 integers for boolean columns; PostgreSQL returns real booleans.</summary>
internal sealed class BoolHandler : SqlMapper.TypeHandler<bool>
{
    public override void SetValue(IDbDataParameter parameter, bool value)
    {
        parameter.DbType = DbType.Boolean;
        parameter.Value = value;
    }

    public override bool Parse(object value) => value switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        short s => s != 0,
        string str => str is "1" or "true" or "True",
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
    };
}
