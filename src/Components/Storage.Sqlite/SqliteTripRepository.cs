using TravelVault.Core.Trips;
using static TravelVault.Storage.Sqlite.SqlHelpers;

namespace TravelVault.Storage.Sqlite;

public sealed class SqliteTripRepository(VaultDatabase db) : ITripRepository
{
    public Task<Trip?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT json FROM trips WHERE id = $id", r => FromJson<Trip>(r.GetString(0)), ("$id", id)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<Trip>> ListAsync(string familyId, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<Trip>>(
            c => c.Query("SELECT json FROM trips WHERE family_id = $f AND is_deleted = 0", r => FromJson<Trip>(r.GetString(0)), ("$f", familyId)),
            cancellationToken);

    public Task SaveAsync(Trip t, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            """
            INSERT INTO trips (id, family_id, version, is_deleted, json) VALUES ($id, $f, $v, $del, $json)
            ON CONFLICT (id) DO UPDATE SET version = $v, is_deleted = $del, json = $json
            """,
            null,
            ("$id", t.Id), ("$f", t.FamilyId), ("$v", t.Version.ToString()), ("$del", Bool(t.IsDeleted)), ("$json", ToJson(t))), cancellationToken);
}
