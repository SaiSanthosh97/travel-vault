namespace TravelVault.Core.Travelers;

public interface ITravelerRepository
{
    Task<Traveler?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Traveler>> ListAsync(string familyId, bool includeDeleted = false, CancellationToken cancellationToken = default);

    Task SaveAsync(Traveler traveler, CancellationToken cancellationToken = default);
}
