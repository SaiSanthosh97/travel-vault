using Microsoft.Data.Sqlite;

namespace TravelVault.Storage.Sqlite;

/// <summary>
/// The encrypted on-device database. The whole file (tables, indexes, search index, journal) is encrypted by
/// SQLite3 Multiple Ciphers (default cipher: ChaCha20-Poly1305) with a key derived from the device master key.
/// One connection, used one operation at a time: simple, safe, and fast enough for a phone-sized vault.
/// </summary>
public sealed class VaultDatabase : IAsyncDisposable, IDisposable
{
    private static int _initialized;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VaultDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (or creates) the database with the given 32-byte key and brings the schema up to date.</summary>
    /// <exception cref="WrongKeyException">The file exists but was encrypted with a different key (or is corrupt).</exception>
    public static VaultDatabase Open(string path, byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The database key must be 32 bytes.", nameof(key));
        }

        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            using (var command = connection.CreateCommand())
            {
                // Hex key: the raw 256-bit key is used directly (no password stretching needed for a random key).
                command.CommandText = $"PRAGMA hexkey = '{Convert.ToHexString(key)}';";
                command.ExecuteNonQuery();
            }

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT count(*) FROM sqlite_master;";
                check.ExecuteScalar();
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
        {
            connection.Dispose();
            throw new WrongKeyException();
        }

        var db = new VaultDatabase(connection);
        Migrations.Apply(connection);
        db.Execute("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA secure_delete = ON;");
        return db;
    }

    /// <summary>Runs work with exclusive use of the connection.</summary>
    public async Task<T> UseAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task UseAsync(Action<SqliteConnection> work, CancellationToken cancellationToken = default) =>
        UseAsync(c =>
        {
            work(c);
            return true;
        }, cancellationToken);

    /// <summary>Runs work inside a transaction (all or nothing).</summary>
    public Task InTransactionAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken cancellationToken = default) =>
        UseAsync(c =>
        {
            using var transaction = c.BeginTransaction();
            work(c, transaction);
            transaction.Commit();
        }, cancellationToken);

    internal void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class WrongKeyException() : Exception("The vault database could not be opened with this key.");
