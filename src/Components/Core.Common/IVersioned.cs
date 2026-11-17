namespace TravelVault.Core.Common;

/// <summary>
/// A record that is replicated between family phones. Conflicts are settled by <see cref="Version"/>
/// (last writer wins by hybrid timestamp); deletes are kept as tombstones so they replicate too.
/// </summary>
public interface IVersioned
{
    string Id { get; }

    string FamilyId { get; }

    HybridTimestamp Version { get; set; }

    string UpdatedBy { get; set; }

    DateTimeOffset UpdatedAt { get; set; }

    bool IsDeleted { get; }
}

public static class Versioning
{
    /// <summary>Stamps a record after a local change.</summary>
    public static void Touch(this IVersioned record, HybridClock clock, string memberId, DateTimeOffset now)
    {
        record.Version = clock.Next();
        record.UpdatedBy = memberId;
        record.UpdatedAt = now;
    }

    /// <summary>True when <paramref name="incoming"/> should replace <paramref name="local"/>.</summary>
    public static bool Wins(IVersioned incoming, IVersioned? local) => local is null || incoming.Version > local.Version;
}
