namespace TravelVault.Core.Documents;

/// <summary>Persistence for documents (implemented in Storage.Sqlite). Queries never return other families' data.</summary>
public interface IDocumentRepository
{
    Task<TravelDocument?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Live (not deleted) documents of a family.</summary>
    Task<IReadOnlyList<TravelDocument>> ListAsync(string familyId, CancellationToken cancellationToken = default);

    /// <summary>Deleted documents of a family (for "Recently deleted").</summary>
    Task<IReadOnlyList<TravelDocument>> ListDeletedAsync(string familyId, CancellationToken cancellationToken = default);

    /// <summary>Live documents with this PNR or booking reference (normalised), for duplicate detection and boarding-pass linking.</summary>
    Task<IReadOnlyList<TravelDocument>> FindByReferenceAsync(string familyId, string reference, CancellationToken cancellationToken = default);

    /// <summary>The live document holding a file with this SHA-256, if any (exact duplicates).</summary>
    Task<TravelDocument?> FindByFileHashAsync(string familyId, string sha256, CancellationToken cancellationToken = default);

    Task SaveAsync(TravelDocument document, CancellationToken cancellationToken = default);

    /// <summary>Removes tombstones older than the cutoff for good.</summary>
    Task<int> PurgeDeletedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}
