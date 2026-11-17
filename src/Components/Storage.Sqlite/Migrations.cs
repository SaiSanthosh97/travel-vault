using Microsoft.Data.Sqlite;

namespace TravelVault.Storage.Sqlite;

/// <summary>
/// Schema versions, applied in order and tracked in <c>PRAGMA user_version</c>. Never edit a released step:
/// add a new one. Aggregates are stored as one JSON document each (the same JSON that sync sends) plus
/// the columns needed for lookups; the whole file is encrypted, so the JSON is never in plain text on disk.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // 1: core tables
        """
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE families (
            id TEXT PRIMARY KEY, version TEXT NOT NULL, is_deleted INTEGER NOT NULL, json TEXT NOT NULL);

        CREATE TABLE family_keys (
            family_id TEXT NOT NULL, epoch INTEGER NOT NULL, wrapped_key BLOB NOT NULL,
            PRIMARY KEY (family_id, epoch));

        CREATE TABLE travelers (
            id TEXT PRIMARY KEY, family_id TEXT NOT NULL, version TEXT NOT NULL, is_deleted INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE INDEX ix_travelers_family ON travelers (family_id);

        CREATE TABLE documents (
            id TEXT PRIMARY KEY, family_id TEXT NOT NULL, type TEXT NOT NULL, visibility TEXT NOT NULL,
            pnr TEXT, booking_ref TEXT, trip_id TEXT, starts_utc TEXT, ends_utc TEXT,
            version TEXT NOT NULL, is_deleted INTEGER NOT NULL, deleted_at TEXT, json TEXT NOT NULL);
        CREATE INDEX ix_documents_family ON documents (family_id, is_deleted, starts_utc);
        CREATE INDEX ix_documents_pnr ON documents (family_id, pnr);
        CREATE INDEX ix_documents_ref ON documents (family_id, booking_ref);
        CREATE INDEX ix_documents_trip ON documents (trip_id);

        CREATE TABLE document_files (
            file_version_id TEXT PRIMARY KEY, document_id TEXT NOT NULL, family_id TEXT NOT NULL, sha256 TEXT NOT NULL);
        CREATE INDEX ix_document_files_hash ON document_files (family_id, sha256);

        CREATE TABLE file_keys (
            file_version_id TEXT PRIMARY KEY, family_id TEXT NOT NULL, wrapped_key BLOB NOT NULL, stored_at TEXT NOT NULL);

        CREATE TABLE trips (
            id TEXT PRIMARY KEY, family_id TEXT NOT NULL, version TEXT NOT NULL, is_deleted INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE INDEX ix_trips_family ON trips (family_id);

        CREATE VIRTUAL TABLE search_index USING fts5(
            document_id UNINDEXED, family_id UNINDEXED,
            title, provider, refs, places, passengers, notes,
            tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3');
        """,

        // 2: sync bookkeeping
        """
        CREATE TABLE change_log (
            record_type TEXT NOT NULL, record_id TEXT NOT NULL, family_id TEXT NOT NULL, version TEXT NOT NULL, json TEXT NOT NULL,
            PRIMARY KEY (record_type, record_id));
        CREATE INDEX ix_change_log_family ON change_log (family_id);

        CREATE TABLE outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT, family_id TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX ix_outbox_family ON outbox (family_id, id);
        """,
    ];

    public static void Apply(SqliteConnection connection)
    {
        var version = Scalar(connection, "PRAGMA user_version;");
        for (var i = (int)version; i < Steps.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Steps[i] + $"\nPRAGMA user_version = {i + 1};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public static int Latest => Steps.Length;

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }
}
