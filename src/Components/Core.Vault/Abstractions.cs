using TravelVault.Core.Common;
using TravelVault.Core.Security;

namespace TravelVault.Core.Vault;

/// <summary>Small key/value settings (implemented by Storage.Sqlite, inside the encrypted database).</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

/// <summary>Which files this phone holds, and their wrapped per-file keys.</summary>
public interface IFileKeyStore
{
    Task SaveAsync(string fileVersionId, string familyId, byte[] wrappedKey, DateTimeOffset storedAt, CancellationToken cancellationToken = default);

    Task<byte[]?> GetAsync(string fileVersionId, CancellationToken cancellationToken = default);

    Task<HashSet<string>> PresentAsync(string familyId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileVersionId, CancellationToken cancellationToken = default);
}

public enum RecordType
{
    Document,
    Traveler,
    Trip,
    Family,
}

/// <summary>Something in the vault changed. <see cref="FromSync"/> is true when it came from another phone.</summary>
public sealed record VaultChange(string FamilyId, RecordType Type, string RecordId, bool FromSync = false);

/// <summary>
/// Reacts to vault changes: the search index, the sync outbox (only local changes are sent), reminder re-planning,
/// UI refresh. Handlers are independent; one failing doesn't stop the others.
/// </summary>
public interface IVaultChangeHandler
{
    Task HandleAsync(VaultChange change, CancellationToken cancellationToken = default);
}

/// <summary>Fans a change out to every registered handler.</summary>
public sealed class VaultEvents(IEnumerable<IVaultChangeHandler> handlers)
{
    private readonly List<IVaultChangeHandler> _handlers = [.. handlers];

    public event Action<Exception>? HandlerFailed;

    public void Add(IVaultChangeHandler handler) => _handlers.Add(handler);

    public async Task PublishAsync(VaultChange change, CancellationToken cancellationToken = default)
    {
        foreach (var handler in _handlers.ToList())
        {
            try
            {
                await handler.HandleAsync(change, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                HandlerFailed?.Invoke(ex);
            }
        }
    }
}

/// <summary>Asks family phones for a file this phone doesn't have yet (implemented by sync).</summary>
public interface IFileRequester
{
    Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default);
}

/// <summary>Who is using the vault on this phone, and the keys and clocks for this session.</summary>
public sealed class VaultSession(string memberId, string displayName, KeyRing keyRing, DeviceKeyPair deviceKeys, IClock clock)
{
    public string MemberId { get; } = memberId;

    public string DisplayName { get; set; } = displayName;

    public KeyRing KeyRing { get; } = keyRing;

    public DeviceKeyPair DeviceKeys { get; } = deviceKeys;

    public IClock Clock { get; } = clock;

    public HybridClock Versions { get; } = new(clock, memberId);

    public DateTimeOffset Now => Clock.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(Clock.UtcNow.ToLocalTime().DateTime);

    public void Stamp(IVersioned record) => record.Touch(Versions, MemberId, Now);
}
