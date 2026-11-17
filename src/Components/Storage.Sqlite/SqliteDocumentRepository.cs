using TravelVault.Core.Documents;
using static TravelVault.Storage.Sqlite.SqlHelpers;

namespace TravelVault.Storage.Sqlite;

public sealed class SqliteDocumentRepository(VaultDatabase db) : IDocumentRepository
{
    public Task<TravelDocument?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT json FROM documents WHERE id = $id", r => FromJson<TravelDocument>(r.GetString(0)), ("$id", id)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<TravelDocument>> ListAsync(string familyId, CancellationToken cancellationToken = default) =>
        List("WHERE family_id = $f AND is_deleted = 0 ORDER BY starts_utc IS NULL, starts_utc", familyId, cancellationToken);

    public Task<IReadOnlyList<TravelDocument>> ListDeletedAsync(string familyId, CancellationToken cancellationToken = default) =>
        List("WHERE family_id = $f AND is_deleted = 1 AND json NOT LIKE '%\"mergedInto\"%' ORDER BY deleted_at DESC", familyId, cancellationToken);

    public Task<IReadOnlyList<TravelDocument>> FindByReferenceAsync(string familyId, string reference, CancellationToken cancellationToken = default)
    {
        var code = BookingCodes.Normalize(reference);
        return code is null
            ? Task.FromResult<IReadOnlyList<TravelDocument>>([])
            : List("WHERE family_id = $f AND is_deleted = 0 AND (pnr = $code OR booking_ref = $code)", familyId, cancellationToken, ("$code", code));
    }

    public Task<TravelDocument?> FindByFileHashAsync(string familyId, string sha256, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query(
            """
            SELECT d.json FROM document_files f JOIN documents d ON d.id = f.document_id
            WHERE f.family_id = $f AND f.sha256 = $h AND d.is_deleted = 0 LIMIT 1
            """,
            r => FromJson<TravelDocument>(r.GetString(0)),
            ("$f", familyId), ("$h", sha256.ToLowerInvariant())).FirstOrDefault(), cancellationToken);

    public Task SaveAsync(TravelDocument d, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            c.Run(
                """
                INSERT INTO documents (id, family_id, type, visibility, pnr, booking_ref, trip_id, starts_utc, ends_utc, version, is_deleted, deleted_at, json)
                VALUES ($id, $f, $type, $vis, $pnr, $ref, $trip, $start, $end, $v, $del, $delAt, $json)
                ON CONFLICT (id) DO UPDATE SET type = $type, visibility = $vis, pnr = $pnr, booking_ref = $ref, trip_id = $trip,
                  starts_utc = $start, ends_utc = $end, version = $v, is_deleted = $del, deleted_at = $delAt, json = $json
                """,
                t,
                ("$id", d.Id), ("$f", d.FamilyId), ("$type", d.Type.ToString()), ("$vis", d.Visibility.ToString()),
                ("$pnr", BookingCodes.Normalize(d.Pnr)), ("$ref", BookingCodes.Normalize(d.BookingReference)), ("$trip", d.TripId),
                ("$start", Utc(d.StartsUtc)), ("$end", Utc(d.EndsUtc)), ("$v", d.Version.ToString()), ("$del", Bool(d.IsDeleted)),
                ("$delAt", Utc(d.DeletedAt)), ("$json", ToJson(d)));

            c.Run("DELETE FROM document_files WHERE document_id = $id", t, ("$id", d.Id));
            foreach (var file in d.Files)
            {
                c.Run(
                    "INSERT OR REPLACE INTO document_files (file_version_id, document_id, family_id, sha256) VALUES ($fv, $id, $f, $h)",
                    t,
                    ("$fv", file.Id), ("$id", d.Id), ("$f", d.FamilyId), ("$h", file.Sha256.ToLowerInvariant()));
            }
        }, cancellationToken);

    public Task<int> PurgeDeletedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
        db.UseAsync(c =>
        {
            c.Run("DELETE FROM document_files WHERE document_id IN (SELECT id FROM documents WHERE is_deleted = 1 AND deleted_at < $cut)", null, ("$cut", Utc(olderThan)));
            c.Run("DELETE FROM search_index WHERE document_id IN (SELECT id FROM documents WHERE is_deleted = 1 AND deleted_at < $cut)", null, ("$cut", Utc(olderThan)));
            return c.Run("DELETE FROM documents WHERE is_deleted = 1 AND deleted_at < $cut", null, ("$cut", Utc(olderThan)));
        }, cancellationToken);

    private Task<IReadOnlyList<TravelDocument>> List(string where, string familyId, CancellationToken cancellationToken, params (string, object?)[] extra) =>
        db.UseAsync<IReadOnlyList<TravelDocument>>(
            c => c.Query("SELECT json FROM documents " + where, r => FromJson<TravelDocument>(r.GetString(0)), [("$f", familyId), .. extra]),
            cancellationToken);
}
