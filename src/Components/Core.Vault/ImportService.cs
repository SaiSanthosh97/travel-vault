using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Duplicates;
using TravelVault.Core.Parsing;
using TravelVault.Core.Security;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;
using F = TravelVault.Core.Documents.TravelDocument.Fields;

namespace TravelVault.Core.Vault;

public enum ImportStatus
{
    /// <summary>Parsed; show the review card (or save straight away when nothing needs review).</summary>
    Ready,

    /// <summary>The exact same file is already in this family's vault.</summary>
    AlreadyInVault,

    /// <summary>Not a PDF, image or text.</summary>
    Unsupported,

    TooLarge,
}

/// <summary>What the user decided for one printed name on the review card.</summary>
public sealed record NameDecision(string RawName, string? TravelerId, bool CreateNew = false, bool Ignore = false);

/// <summary>An imported file waiting to be saved: the proposed document plus everything the review card needs.</summary>
public sealed class ImportDraft
{
    public required TravelDocument Document { get; init; }

    public required byte[] Content { get; init; }

    public required string MediaType { get; init; }

    public required string Sha256 { get; init; }

    public string? FileName { get; init; }

    public required ParsedDocument Parsed { get; init; }

    public required IReadOnlyList<PassengerMatch> Names { get; init; }

    public required IReadOnlyList<DuplicateCandidate> Duplicates { get; init; }

    /// <summary>True when everything important was read confidently, so saving needs no questions.</summary>
    public bool CanSaveWithoutReview => !Parsed.NeedsReview && Names.All(n => n.IsConfident) && Duplicates.Count == 0;

    /// <summary>The default answer for each name: the matched traveler, or a new traveler profile.</summary>
    public IReadOnlyList<NameDecision> DefaultDecisions() =>
        Names.Select(n => n.Traveler is not null ? new NameDecision(n.RawName, n.Traveler.Id) : new NameDecision(n.RawName, null, CreateNew: true)).ToList();
}

public sealed record ImportResult(ImportStatus Status, ImportDraft? Draft = null, TravelDocument? Existing = null);

/// <summary>
/// The import use case: file in → exact-duplicate check → read text/barcodes → parse → match names → find fuzzy
/// duplicates → draft. Then <see cref="SaveAsync"/>: create travelers for new names, learn aliases, encrypt and store
/// the file, link boarding passes, save, and announce the change (search, sync, reminders react).
/// </summary>
public sealed class ImportService(
    VaultSession session,
    ContentExtractor extractor,
    IDocumentRepository documents,
    ITravelerRepository travelers,
    FileVault files,
    IFileKeyStore fileKeys,
    TravelerService travelerService,
    VaultEvents events)
{
    public async Task<ImportResult> PrepareAsync(string familyId, byte[] content, string? fileName, CancellationToken cancellationToken = default)
    {
        if (content.LongLength > ContentExtractor.MaxFileSize)
        {
            return new ImportResult(ImportStatus.TooLarge);
        }

        var mediaType = MediaTypes.Detect(content);
        if (mediaType is null)
        {
            return new ImportResult(ImportStatus.Unsupported);
        }

        var sha = Convert.ToHexStringLower(FileVault.Sha256(content));
        if (await documents.FindByFileHashAsync(familyId, sha, cancellationToken) is { } existing)
        {
            return new ImportResult(ImportStatus.AlreadyInVault, Existing: existing);
        }

        var extracted = await extractor.ExtractAsync(content, mediaType, fileName, cancellationToken);
        var parsed = TravelDocumentParser.Parse(extracted, session.Today);
        var people = await travelers.ListAsync(familyId, cancellationToken: cancellationToken);
        var names = parsed.PassengerNames.Select(n => PassengerMatcher.Match(n, people)).ToList();

        var doc = ToDocument(familyId, parsed);
        // Provisional passengers (matched ones) so fuzzy duplicate detection can compare people.
        doc.Passengers = names.Where(n => n.Traveler is not null)
            .Select(n => new DocumentPassenger(n.Traveler!.Id, n.RawName, Map(n.Strength)))
            .DistinctBy(p => p.TravelerId).ToList();
        if (doc.Passengers.Count == 0 && names.Count == 0)
        {
            var me = await travelerService.MyTravelerAsync(familyId, cancellationToken);
            doc.SetPassengers([], me.Id);
        }

        var existingDocs = await documents.ListAsync(familyId, cancellationToken);
        var duplicates = DuplicateDetector.Find(doc, existingDocs);

        return new ImportResult(ImportStatus.Ready, new ImportDraft
        {
            Document = doc,
            Content = content,
            MediaType = mediaType,
            Sha256 = sha,
            FileName = fileName,
            Parsed = parsed,
            Names = names,
            Duplicates = duplicates,
        });
    }

    /// <param name="decisions">One per printed name (defaults: <see cref="ImportDraft.DefaultDecisions"/>).</param>
    /// <param name="mergeInto">Merge into this existing document instead of saving a new one (duplicate prompt).</param>
    public async Task<TravelDocument> SaveAsync(ImportDraft draft, IReadOnlyList<NameDecision>? decisions = null, string? mergeInto = null, CancellationToken cancellationToken = default)
    {
        var doc = draft.Document;
        var familyId = doc.FamilyId;
        decisions ??= draft.DefaultDecisions();

        // 1. Travelers: create new profiles, learn aliases for non-exact matches.
        var passengers = new List<DocumentPassenger>();
        foreach (var decision in decisions.Where(d => !d.Ignore))
        {
            Traveler? traveler = decision.TravelerId is { } id ? await travelers.GetAsync(id, cancellationToken) : null;
            if (traveler is null && decision.CreateNew)
            {
                traveler = await travelerService.CreateAsync(familyId, PersonName.Parse(decision.RawName).ToTitleCase(), cancellationToken: cancellationToken);
            }

            if (traveler is null)
            {
                continue;
            }

            var match = draft.Names.FirstOrDefault(n => n.RawName == decision.RawName);
            if (match is null || match.Traveler?.Id != traveler.Id || match.Strength != MatchStrength.Exact)
            {
                await travelerService.LearnAliasAsync(traveler, decision.RawName, cancellationToken);
            }

            var confidence = match?.Traveler?.Id == traveler.Id ? Map(match.Strength) : Confidence.UserEdited;
            passengers.Add(new DocumentPassenger(traveler.Id, decision.RawName, confidence));
        }

        // No name on the ticket (or all ignored): it belongs to the uploader.
        var fallback = passengers.Count == 0 ? (await travelerService.MyTravelerAsync(familyId, cancellationToken)).Id : "";
        doc.SetPassengers(passengers, fallback);
        foreach (var segment in doc.Segments)
        {
            segment.TravelerIds = doc.Passengers.Select(p => p.TravelerId).ToList();
        }

        // 2. The file: encrypted with its own key; the key wrapped by the device key ring.
        var file = new FileVersion
        {
            Id = Ids.New(),
            Sha256 = draft.Sha256,
            Size = draft.Content.LongLength,
            MediaType = draft.MediaType,
            OriginalName = draft.FileName ?? "document" + MediaTypes.Extension(draft.MediaType),
            AddedAt = session.Now,
            AddedBy = session.MemberId,
        };
        var fileKey = await files.WriteAsync(familyId, file.Id, draft.Content, cancellationToken);
        await fileKeys.SaveAsync(file.Id, familyId, session.KeyRing.Protect(fileKey, "file/" + file.Id), session.Now, cancellationToken);
        doc.AddFile(file, makePrimary: true);

        // 3. Boarding pass → ticket link.
        var existing = await documents.ListAsync(familyId, cancellationToken);
        if (doc.Type == DocumentType.BoardingPass)
        {
            doc.BoardingPass = BoardingPassLinker.FindTarget(doc, existing);
        }

        DocumentTitles.Refresh(doc);

        // 4. Merge into an existing document, or save as new.
        if (mergeInto is not null && await documents.GetAsync(mergeInto, cancellationToken) is { IsDeleted: false } target)
        {
            DocumentMerger.Merge(target, doc, session.Now);
            session.Stamp(target);
            await documents.SaveAsync(target, cancellationToken);
            await events.PublishAsync(new VaultChange(familyId, RecordType.Document, target.Id), cancellationToken);
            return target;
        }

        session.Stamp(doc);
        await documents.SaveAsync(doc, cancellationToken);
        await events.PublishAsync(new VaultChange(familyId, RecordType.Document, doc.Id), cancellationToken);
        return doc;
    }

    private TravelDocument ToDocument(string familyId, ParsedDocument parsed)
    {
        var doc = TravelDocument.Create(familyId, parsed.Type, session.MemberId, session.Now);
        doc.Provider = parsed.Provider;
        doc.Pnr = BookingCodes.Normalize(parsed.Pnr);
        doc.BookingReference = BookingCodes.Normalize(parsed.BookingReference) ?? parsed.BookingReference;
        doc.Identity = parsed.Identity;
        doc.Segments = [.. parsed.Segments];
        foreach (var (field, confidence) in parsed.Confidence)
        {
            doc.FieldConfidence[field] = confidence;
        }

        if (parsed.Warnings.Count > 0)
        {
            doc.FieldConfidence[F.Segments] = Confidence.Low;
        }

        DocumentTitles.Refresh(doc);
        return doc;
    }

    private static Confidence Map(MatchStrength strength) => strength switch
    {
        MatchStrength.Exact => Confidence.High,
        MatchStrength.Good => Confidence.Medium,
        _ => Confidence.Low,
    };
}
