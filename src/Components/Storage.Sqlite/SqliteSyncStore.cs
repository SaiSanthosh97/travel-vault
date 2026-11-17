using TravelVault.Core.Sync;
using static TravelVault.Storage.Sqlite.SqlHelpers;

namespace TravelVault.Storage.Sqlite;

public sealed class SqliteSyncStore(VaultDatabase db) : ISyncStore
{
    public Task SaveChangeAsync(SignedChange change, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            // The version column sorts as text, so "only if newer" is a plain comparison.
            """
            INSERT INTO change_log (record_type, record_id, family_id, version, json) VALUES ($t, $id, $f, $v, $json)
            ON CONFLICT (record_type, record_id) DO UPDATE SET version = $v, json = $json, family_id = $f WHERE excluded.version > change_log.version
            """,
            null,
            ("$t", change.Type), ("$id", change.Id), ("$f", change.FamilyId), ("$v", change.Version), ("$json", ToJson(change))), cancellationToken);

    public Task<SignedChange?> GetChangeAsync(string type, string id, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query(
            "SELECT json FROM change_log WHERE record_type = $t AND record_id = $id",
            r => FromJson<SignedChange>(r.GetString(0)),
            ("$t", type), ("$id", id)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<SignedChange>> ListChangesAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<SignedChange>>(
            // Families first (members' keys are needed to verify everything else), then by version.
            c => c.Query(
                "SELECT json FROM change_log WHERE family_id = $f ORDER BY record_type <> 'fam', version",
                r => FromJson<SignedChange>(r.GetString(0)),
                ("$f", familyId)),
            cancellationToken);

    public Task EnqueueAsync(string familyId, string kind, string payload, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            "INSERT INTO outbox (family_id, kind, payload, created_at) VALUES ($f, $k, $p, $at)",
            null,
            ("$f", familyId), ("$k", kind), ("$p", payload), ("$at", Utc(DateTimeOffset.UtcNow))), cancellationToken);

    public Task<IReadOnlyList<OutboxItem>> OutboxAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<OutboxItem>>(
            c => c.Query(
                "SELECT id, family_id, kind, payload FROM outbox WHERE family_id = $f ORDER BY id",
                r => new OutboxItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)),
                ("$f", familyId)),
            cancellationToken);

    public Task RemoveOutboxAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            foreach (var id in ids)
            {
                c.Run("DELETE FROM outbox WHERE id = $id", t, ("$id", id));
            }
        }, cancellationToken);

    public Task DeleteFamilyAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            c.Run("DELETE FROM change_log WHERE family_id = $f", t, ("$f", familyId));
            c.Run("DELETE FROM outbox WHERE family_id = $f", t, ("$f", familyId));
        }, cancellationToken);
}
