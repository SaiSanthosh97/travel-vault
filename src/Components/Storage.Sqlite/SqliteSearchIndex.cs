using TravelVault.Core.Search;

namespace TravelVault.Storage.Sqlite;

/// <summary>
/// FTS5 index inside the encrypted database. Ranking uses bm25 with column weights: references (PNR, flight
/// numbers) and places matter most, then passengers, title and provider, then notes.
/// </summary>
public sealed class SqliteSearchIndex(VaultDatabase db) : ISearchIndex
{
    public Task UpsertAsync(SearchEntry e, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            c.Run("DELETE FROM search_index WHERE document_id = $id", t, ("$id", e.DocumentId));
            c.Run(
                "INSERT INTO search_index (document_id, family_id, title, provider, refs, places, passengers, notes) VALUES ($id, $f, $title, $provider, $refs, $places, $people, $notes)",
                t,
                ("$id", e.DocumentId), ("$f", e.FamilyId), ("$title", e.Title), ("$provider", e.Provider), ("$refs", e.Refs),
                ("$places", e.Places), ("$people", e.Passengers), ("$notes", e.Notes));
        }, cancellationToken);

    public Task RemoveAsync(string documentId, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run("DELETE FROM search_index WHERE document_id = $id", null, ("$id", documentId)), cancellationToken);

    public Task ClearFamilyAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run("DELETE FROM search_index WHERE family_id = $f", null, ("$f", familyId)), cancellationToken);

    public Task<IReadOnlyList<string>> QueryAsync(string familyId, string ftsQuery, int limit = 200, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<string>>(
            c => c.Query(
                """
                SELECT document_id FROM search_index
                WHERE search_index MATCH $q AND family_id = $f
                ORDER BY bm25(search_index, 0, 0, 2.0, 2.0, 5.0, 4.0, 3.0, 1.0)
                LIMIT $limit
                """,
                r => r.GetString(0),
                ("$q", ftsQuery), ("$f", familyId), ("$limit", limit)),
            cancellationToken);
}
