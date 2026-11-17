namespace TravelVault.Core.Families;

/// <summary>Families, plus their secret keys (stored wrapped by the device key ring, inside the encrypted database).</summary>
public interface IFamilyRepository
{
    Task<Family?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Family>> ListAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Family family, CancellationToken cancellationToken = default);

    /// <summary>Removes a family and all its data from this phone (leave / removed / delete).</summary>
    Task DeleteEverythingAsync(string familyId, CancellationToken cancellationToken = default);

    /// <param name="wrappedKey">The family key for this epoch, already protected by the key ring.</param>
    Task SaveKeyAsync(string familyId, int epoch, byte[] wrappedKey, CancellationToken cancellationToken = default);

    Task<byte[]?> GetKeyAsync(string familyId, int epoch, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, byte[]>> GetAllKeysAsync(string familyId, CancellationToken cancellationToken = default);
}
