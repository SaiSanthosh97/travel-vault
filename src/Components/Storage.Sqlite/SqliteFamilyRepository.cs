using TravelVault.Core.Families;
using static TravelVault.Storage.Sqlite.SqlHelpers;

namespace TravelVault.Storage.Sqlite;

public sealed class SqliteFamilyRepository(VaultDatabase db) : IFamilyRepository
{
    public Task<Family?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT json FROM families WHERE id = $id AND is_deleted = 0", r => FromJson<Family>(r.GetString(0)), ("$id", id)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<Family>> ListAsync(CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<Family>>(
            c => c.Query("SELECT json FROM families WHERE is_deleted = 0", r => FromJson<Family>(r.GetString(0)))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            cancellationToken);

    public Task SaveAsync(Family f, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            """
            INSERT INTO families (id, version, is_deleted, json) VALUES ($id, $v, $del, $json)
            ON CONFLICT (id) DO UPDATE SET version = $v, is_deleted = $del, json = $json
            """,
            null,
            ("$id", f.Id), ("$v", f.Version.ToString()), ("$del", Bool(f.IsDeleted)), ("$json", ToJson(f))), cancellationToken);

    public Task DeleteEverythingAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            foreach (var table in new[] { "search_index", "document_files", "file_keys", "documents", "travelers", "trips", "family_keys" })
            {
                c.Run($"DELETE FROM {table} WHERE family_id = $f", t, ("$f", familyId));
            }

            c.Run("DELETE FROM families WHERE id = $f", t, ("$f", familyId));
            c.Run("DELETE FROM settings WHERE key LIKE $prefix", t, ("$prefix", $"family.{familyId}.%"));
        }, cancellationToken);

    public Task SaveKeyAsync(string familyId, int epoch, byte[] wrappedKey, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            "INSERT OR REPLACE INTO family_keys (family_id, epoch, wrapped_key) VALUES ($f, $e, $k)",
            null,
            ("$f", familyId), ("$e", epoch), ("$k", wrappedKey)), cancellationToken);

    public Task<byte[]?> GetKeyAsync(string familyId, int epoch, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query(
            "SELECT wrapped_key FROM family_keys WHERE family_id = $f AND epoch = $e",
            r => (byte[])r.GetValue(0),
            ("$f", familyId), ("$e", epoch)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyDictionary<int, byte[]>> GetAllKeysAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyDictionary<int, byte[]>>(
            c => c.Query("SELECT epoch, wrapped_key FROM family_keys WHERE family_id = $f", r => (Epoch: r.GetInt32(0), Key: (byte[])r.GetValue(1)), ("$f", familyId))
                .ToDictionary(x => x.Epoch, x => x.Key),
            cancellationToken);
}
