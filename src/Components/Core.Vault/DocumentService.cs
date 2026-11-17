using TravelVault.Core.Documents;
using TravelVault.Core.Security;
using TravelVault.Core.Trips;

namespace TravelVault.Core.Vault;

public enum FileAvailability
{
    /// <summary>On this phone: opens instantly, offline.</summary>
    Present,

    /// <summary>Not received yet; a request was sent to family phones.</summary>
    Requested,
}

public sealed record OpenedFile(FileAvailability Availability, byte[]? Content, FileVersion File);

/// <summary>Document use cases: list, edit, visibility, delete/restore, merge, reassign travelers, open the original file.</summary>
public sealed class DocumentService(
    VaultSession session,
    IDocumentRepository documents,
    FileVault files,
    IFileKeyStore fileKeys,
    IFileRequester fileRequester,
    VaultEvents events)
{
    public static readonly TimeSpan KeepDeletedFor = TimeSpan.FromDays(30);

    public Task<TravelDocument?> GetAsync(string id, CancellationToken cancellationToken = default) => documents.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<TravelDocument>> ListAsync(string familyId, CancellationToken cancellationToken = default) => documents.ListAsync(familyId, cancellationToken);

    public Task<IReadOnlyList<TravelDocument>> RecentlyDeletedAsync(string familyId, CancellationToken cancellationToken = default) => documents.ListDeletedAsync(familyId, cancellationToken);

    /// <summary>The next segment to use (home screen "Next up"): the earliest one not yet departed (or departed within 2 hours).</summary>
    public async Task<(TravelDocument Document, Segment Segment)?> NextUpAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var now = session.Now;
        return (await documents.ListAsync(familyId, cancellationToken))
            .Where(d => d.Type is not (DocumentType.Visa or DocumentType.Passport))
            .Where(d => d.Type != DocumentType.BoardingPass || d.BoardingPass is null)
            .SelectMany(d => d.Segments.Select(s => (Document: d, Segment: s)))
            .Where(x => x.Segment.DepartsUtc is { } t && t > now.AddHours(-2))
            .OrderBy(x => x.Segment.DepartsUtc)
            .Select(x => ((TravelDocument, Segment)?)(x.Document, x.Segment))
            .FirstOrDefault();
    }

    /// <summary>Saves user edits. <paramref name="editedFields"/> become UserEdited (protected from automatic changes).</summary>
    public async Task UpdateAsync(TravelDocument document, IEnumerable<string> editedFields, CancellationToken cancellationToken = default)
    {
        foreach (var field in editedFields)
        {
            document.MarkEdited(field);
        }

        var problems = document.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        DocumentTitles.Refresh(document);
        await SaveAsync(document, cancellationToken);
    }

    public async Task ReassignTravelersAsync(string documentId, IReadOnlyList<string> travelerIds, CancellationToken cancellationToken = default)
    {
        if (travelerIds.Count == 0)
        {
            throw new InvalidOperationException("A document needs at least one traveler.");
        }

        var doc = await Required(documentId, cancellationToken);
        doc.Passengers = travelerIds.Distinct().Select(id => new DocumentPassenger(id, doc.Passengers.FirstOrDefault(p => p.TravelerId == id)?.RawName, Confidence.UserEdited)).ToList();
        foreach (var segment in doc.Segments)
        {
            segment.TravelerIds = [.. travelerIds];
        }

        doc.MarkEdited(TravelDocument.Fields.Passengers);
        await SaveAsync(doc, cancellationToken);
    }

    /// <summary>
    /// Family ↔ Private. A document made private is withdrawn from the other phones (they receive a tombstone);
    /// it stays on this phone. Copies already on other phones are removed by their app, which is the best a
    /// serverless system can do — the UI says so.
    /// </summary>
    public async Task SetVisibilityAsync(string documentId, Visibility visibility, CancellationToken cancellationToken = default)
    {
        var doc = await Required(documentId, cancellationToken);
        if (doc.Visibility == visibility)
        {
            return;
        }

        if (visibility == Visibility.Private && doc.OwnerMemberId != session.MemberId)
        {
            throw new InvalidOperationException("Only the person who added a document can make it private.");
        }

        doc.Visibility = visibility;
        await SaveAsync(doc, cancellationToken);
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var doc = await Required(documentId, cancellationToken);
        doc.Delete(session.Now);
        await SaveAsync(doc, cancellationToken);
    }

    public async Task RestoreAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var doc = await Required(documentId, cancellationToken);
        doc.Restore();
        await SaveAsync(doc, cancellationToken);
    }

    /// <summary>Merges a duplicate into the document to keep (see <see cref="DocumentMerger"/>).</summary>
    public async Task<TravelDocument> MergeAsync(string keepId, string duplicateId, CancellationToken cancellationToken = default)
    {
        var keep = await Required(keepId, cancellationToken);
        var duplicate = await Required(duplicateId, cancellationToken);
        DocumentMerger.Merge(keep, duplicate, session.Now);
        await SaveAsync(keep, cancellationToken);
        await SaveAsync(duplicate, cancellationToken);
        return keep;
    }

    public async Task SetPrimaryFileAsync(string documentId, string fileVersionId, CancellationToken cancellationToken = default)
    {
        var doc = await Required(documentId, cancellationToken);
        doc.SetPrimaryFile(fileVersionId);
        await SaveAsync(doc, cancellationToken);
    }

    /// <summary>
    /// Opens an original file. If this phone doesn't have it yet, asks the family's phones for it right away
    /// ("Getting it from family…"); once it arrives it is kept on the phone for offline use.
    /// </summary>
    public async Task<OpenedFile> OpenFileAsync(string documentId, string? fileVersionId = null, CancellationToken cancellationToken = default)
    {
        var doc = await Required(documentId, cancellationToken);
        var file = (fileVersionId is null ? doc.PrimaryFile : doc.Files.FirstOrDefault(f => f.Id == fileVersionId))
            ?? throw new InvalidOperationException("This document has no file.");

        if (await fileKeys.GetAsync(file.Id, cancellationToken) is { } wrapped
            && session.KeyRing.Unprotect(wrapped, "file/" + file.Id) is { } key
            && await files.ReadAsync(doc.FamilyId, file.Id, key, cancellationToken) is { } content)
        {
            return new OpenedFile(FileAvailability.Present, content, file);
        }

        await fileRequester.RequestAsync(doc.FamilyId, file.Id, cancellationToken);
        return new OpenedFile(FileAvailability.Requested, null, file);
    }

    /// <summary>Stores a file received from another phone (after its hash was verified by sync).</summary>
    public async Task StoreReceivedFileAsync(string familyId, string fileVersionId, byte[] content, CancellationToken cancellationToken = default)
    {
        var key = await files.WriteAsync(familyId, fileVersionId, content, cancellationToken);
        await fileKeys.SaveAsync(fileVersionId, familyId, session.KeyRing.Protect(key, "file/" + fileVersionId), session.Now, cancellationToken);
    }

    /// <summary>Reads a file this phone has (for sending it to family phones). Null if missing.</summary>
    public async Task<byte[]?> ReadLocalFileAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default)
    {
        if (await fileKeys.GetAsync(fileVersionId, cancellationToken) is not { } wrapped
            || session.KeyRing.Unprotect(wrapped, "file/" + fileVersionId) is not { } key)
        {
            return null;
        }

        return await files.ReadAsync(familyId, fileVersionId, key, cancellationToken);
    }

    /// <summary>Permanently removes documents deleted more than 30 days ago (and their files).</summary>
    public async Task<int> PurgeOldDeletedAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var cutoff = session.Now - KeepDeletedFor;
        foreach (var doc in await documents.ListDeletedAsync(familyId, cancellationToken))
        {
            if (doc.DeletedAt < cutoff && doc.MergedInto is null)
            {
                foreach (var file in doc.Files)
                {
                    files.Delete(familyId, file.Id);
                    await fileKeys.DeleteAsync(file.Id, cancellationToken);
                }
            }
        }

        return await documents.PurgeDeletedAsync(cutoff, cancellationToken);
    }

    /// <summary>Boarding passes linked to a ticket (shown together with it).</summary>
    public async Task<IReadOnlyList<TravelDocument>> BoardingPassesForAsync(TravelDocument ticket, CancellationToken cancellationToken = default) =>
        BoardingPassLinker.LinkedTo(ticket, await documents.ListAsync(ticket.FamilyId, cancellationToken));

    private async Task<TravelDocument> Required(string id, CancellationToken cancellationToken) =>
        await documents.GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("Document not found.");

    private async Task SaveAsync(TravelDocument document, CancellationToken cancellationToken)
    {
        session.Stamp(document);
        await documents.SaveAsync(document, cancellationToken);
        await events.PublishAsync(new VaultChange(document.FamilyId, RecordType.Document, document.Id), cancellationToken);
    }
}
