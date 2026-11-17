using TravelVault.Core.Vault;
using static TravelVault.Storage.Sqlite.SqlHelpers;

namespace TravelVault.Storage.Sqlite;

/// <summary>Small settings and sync bookmarks ("family.{id}.lastSync" etc.), inside the encrypted database.</summary>
public sealed class SqliteSettings(VaultDatabase db) : ISettingsStore
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT value FROM settings WHERE key = $k", r => r.GetString(0), ("$k", key)).FirstOrDefault(), cancellationToken);

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default) =>
        value is null
            ? db.UseAsync(c => c.Run("DELETE FROM settings WHERE key = $k", null, ("$k", key)), cancellationToken)
            : db.UseAsync(c => c.Run("INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)", null, ("$k", key), ("$v", value)), cancellationToken);
}

/// <summary>
/// The per-file encryption keys (wrapped by the key ring) for files present on this phone.
/// A row here means "this phone has the file"; no row means it must be fetched from family.
/// </summary>
public sealed class SqliteFileKeyStore(VaultDatabase db) : IFileKeyStore
{
    public Task SaveAsync(string fileVersionId, string familyId, byte[] wrappedKey, DateTimeOffset storedAt, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            "INSERT OR REPLACE INTO file_keys (file_version_id, family_id, wrapped_key, stored_at) VALUES ($id, $f, $k, $at)",
            null,
            ("$id", fileVersionId), ("$f", familyId), ("$k", wrappedKey), ("$at", Utc(storedAt))), cancellationToken);

    public Task<byte[]?> GetAsync(string fileVersionId, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT wrapped_key FROM file_keys WHERE file_version_id = $id", r => (byte[])r.GetValue(0), ("$id", fileVersionId)).FirstOrDefault(), cancellationToken);

    public Task<HashSet<string>> PresentAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT file_version_id FROM file_keys WHERE family_id = $f", r => r.GetString(0), ("$f", familyId)).ToHashSet(), cancellationToken);

    public Task DeleteAsync(string fileVersionId, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run("DELETE FROM file_keys WHERE file_version_id = $id", null, ("$id", fileVersionId)), cancellationToken);
}
