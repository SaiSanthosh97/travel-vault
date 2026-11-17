namespace TravelVault.Core.Trips;

public interface ITripRepository
{
    Task<Trip?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Trip>> ListAsync(string familyId, CancellationToken cancellationToken = default);

    Task SaveAsync(Trip trip, CancellationToken cancellationToken = default);
}
