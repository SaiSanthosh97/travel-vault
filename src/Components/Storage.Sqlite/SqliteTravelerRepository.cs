using TravelVault.Core.Travelers;
using static TravelVault.Storage.Sqlite.SqlHelpers;

namespace TravelVault.Storage.Sqlite;

public sealed class SqliteTravelerRepository(VaultDatabase db) : ITravelerRepository
{
    public Task<Traveler?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT json FROM travelers WHERE id = $id", r => FromJson<Traveler>(r.GetString(0)), ("$id", id)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<Traveler>> ListAsync(string familyId, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<Traveler>>(
            c => c.Query(
                "SELECT json FROM travelers WHERE family_id = $f" + (includeDeleted ? "" : " AND is_deleted = 0"),
                r => FromJson<Traveler>(r.GetString(0)),
                ("$f", familyId)).OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            cancellationToken);

    public Task SaveAsync(Traveler t, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            """
            INSERT INTO travelers (id, family_id, version, is_deleted, json) VALUES ($id, $f, $v, $del, $json)
            ON CONFLICT (id) DO UPDATE SET version = $v, is_deleted = $del, json = $json
            """,
            null,
            ("$id", t.Id), ("$f", t.FamilyId), ("$v", t.Version.ToString()), ("$del", Bool(t.IsDeleted)), ("$json", ToJson(t))), cancellationToken);
}
