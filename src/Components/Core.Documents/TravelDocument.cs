using TravelVault.Core.Common;

namespace TravelVault.Core.Documents;

/// <summary>One leg of a journey (flight, train, bus), a hotel stay or an event slot.</summary>
public sealed class Segment
{
    public required string Id { get; init; }

    public TravelMode Mode { get; set; }

    /// <summary>Departure place; for a stay, the hotel; for an event, the venue.</summary>
    public required Place From { get; set; }

    /// <summary>Arrival place (null for stays and events).</summary>
    public Place? To { get; set; }

    /// <summary>Departure / check-in / event start.</summary>
    public LocalDateTimeZ? Departs { get; set; }

    /// <summary>Arrival / check-out / event end.</summary>
    public LocalDateTimeZ? Arrives { get; set; }

    public string? CarrierCode { get; set; }

    /// <summary>Flight / train / bus number, e.g. "6E 2345", "12627".</summary>
    public string? ServiceNumber { get; set; }

    /// <summary>Name of the service when printed, e.g. the train name "Karnataka Express".</summary>
    public string? ServiceName { get; set; }

    public string? Seat { get; set; }

    public string? TravelClass { get; set; }

    /// <summary>Travelers on this leg (subset of the document's passengers; empty = all of them).</summary>
    public List<string> TravelerIds { get; set; } = [];

    public DateTimeOffset? DepartsUtc => Departs?.ToUtc();

    public DateTimeOffset? ArrivesUtc => Arrives?.ToUtc();
}

/// <summary>An extra fact printed on the ticket ("Quota" → "Tatkal", "Terminal" → "1B"). Shown on the ticket card.</summary>
public sealed record DocumentDetail(string Label, string Value);

/// <summary>A passenger as printed on the ticket, matched to a traveler profile.</summary>
public sealed record DocumentPassenger(string TravelerId, string? RawName, Confidence Match);

/// <summary>One original file attached to a document. Duplicates and updated e-tickets become versions.</summary>
public sealed class FileVersion
{
    public required string Id { get; init; }

    /// <summary>Lower-case hex SHA-256 of the original (plaintext) bytes: exact-duplicate detection and integrity.</summary>
    public required string Sha256 { get; init; }

    public long Size { get; init; }

    public required string MediaType { get; init; }

    public string? OriginalName { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public string AddedBy { get; init; } = "";

    public bool IsPrimary { get; set; }
}

/// <summary>
/// The central aggregate: one imported travel item with its parsed details.
/// Invariants (checked by <see cref="Validate"/> and kept by the methods):
/// exactly one primary file; unique file hashes; at least one passenger; arrival not before departure;
/// user-edited fields are never overwritten by parsing or merging.
/// </summary>
public sealed class TravelDocument : IVersioned
{
    public required string Id { get; init; }

    public required string FamilyId { get; init; }

    public DocumentType Type { get; set; }

    public string Title { get; set; } = "";

    public Visibility Visibility { get; set; }

    /// <summary>The member who imported it (owner of Private documents; default passenger when no name is printed).</summary>
    public required string OwnerMemberId { get; init; }

    public string? Provider { get; set; }

    public string? BookingReference { get; set; }

    public string? Pnr { get; set; }

    public IdentityInfo? Identity { get; set; }

    public BoardingPassLink? BoardingPass { get; set; }

    public List<Segment> Segments { get; set; } = [];

    public List<DocumentPassenger> Passengers { get; set; } = [];

    public List<FileVersion> Files { get; set; } = [];

    /// <summary>Per-field confidence, keyed by <see cref="Fields"/> names.</summary>
    public Dictionary<string, Confidence> FieldConfidence { get; set; } = [];

    public string? TripId { get; set; }

    public string? Notes { get; set; }

    /// <summary>Extra printed facts read at import (quota, booking status, terminal…). See <see cref="DocumentDetail"/>.</summary>
    public List<DocumentDetail> Details { get; set; } = [];

    /// <summary>Set when this document was merged into another (it is then deleted).</summary>
    public string? MergedInto { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = "";

    public HybridTimestamp Version { get; set; }

    public string UpdatedBy { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public FileVersion? PrimaryFile => Files.FirstOrDefault(f => f.IsPrimary) ?? Files.FirstOrDefault();

    /// <summary>The earliest departure (UTC) — used for ordering, trips and reminders.</summary>
    public DateTimeOffset? StartsUtc => Segments.Select(s => s.DepartsUtc).Where(t => t is not null).Min();

    public DateTimeOffset? EndsUtc => Segments.Select(s => s.ArrivesUtc ?? s.DepartsUtc).Where(t => t is not null).Max();

    /// <summary>Field names for <see cref="FieldConfidence"/>.</summary>
    public static class Fields
    {
        public const string Type = "type";
        public const string Provider = "provider";
        public const string BookingReference = "bookingRef";
        public const string Pnr = "pnr";
        public const string Passengers = "passengers";
        public const string Segments = "segments";
        public const string Identity = "identity";
        public const string Title = "title";
    }

    public static TravelDocument Create(string familyId, DocumentType type, string ownerMemberId, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        FamilyId = familyId,
        Type = type,
        OwnerMemberId = ownerMemberId,
        CreatedAt = now,
        CreatedBy = ownerMemberId,
        UpdatedAt = now,
        UpdatedBy = ownerMemberId,
    };

    public Confidence ConfidenceOf(string field) => FieldConfidence.TryGetValue(field, out var c) ? c : Confidence.Medium;

    public bool IsUserEdited(string field) => ConfidenceOf(field) == Confidence.UserEdited;

    /// <summary>Records a manual edit: the field becomes <see cref="Confidence.UserEdited"/> and is protected from automatic changes.</summary>
    public void MarkEdited(string field) => FieldConfidence[field] = Confidence.UserEdited;

    /// <summary>
    /// Sets the passengers. If none are given (no name printed on the ticket), the uploader's traveler is used,
    /// so every document belongs to someone; any member can reassign later.
    /// </summary>
    public void SetPassengers(IEnumerable<DocumentPassenger> passengers, string uploaderTravelerId)
    {
        var list = passengers.DistinctBy(p => p.TravelerId).ToList();
        if (list.Count == 0)
        {
            list.Add(new DocumentPassenger(uploaderTravelerId, null, Confidence.Low));
        }

        Passengers = list;
    }

    /// <summary>Adds a file version. The first file becomes primary; <paramref name="makePrimary"/> moves primary to the new one.</summary>
    /// <returns>False when a file with the same content is already attached.</returns>
    public bool AddFile(FileVersion file, bool makePrimary)
    {
        if (Files.Any(f => f.Sha256 == file.Sha256))
        {
            return false;
        }

        if (makePrimary || Files.Count == 0)
        {
            foreach (var f in Files)
            {
                f.IsPrimary = false;
            }

            file.IsPrimary = true;
        }

        Files.Add(file);
        return true;
    }

    public void SetPrimaryFile(string fileVersionId)
    {
        if (Files.All(f => f.Id != fileVersionId))
        {
            throw new InvalidOperationException("No such file on this document.");
        }

        foreach (var f in Files)
        {
            f.IsPrimary = f.Id == fileVersionId;
        }
    }

    public void Delete(DateTimeOffset now)
    {
        IsDeleted = true;
        DeletedAt = now;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAt = null;
        MergedInto = null;
    }

    public IReadOnlyList<string> AllTravelerIds() =>
        Passengers.Select(p => p.TravelerId).Concat(Segments.SelectMany(s => s.TravelerIds)).Distinct().ToList();

    /// <returns>Rule violations (empty when valid).</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Files.Count > 0 && Files.Count(f => f.IsPrimary) != 1)
        {
            problems.Add("Exactly one file must be primary.");
        }

        if (Files.Select(f => f.Sha256).Distinct().Count() != Files.Count)
        {
            problems.Add("The same file is attached twice.");
        }

        if (Passengers.Count == 0)
        {
            problems.Add("A document needs at least one traveler.");
        }

        foreach (var s in Segments)
        {
            if (s.DepartsUtc is { } d && s.ArrivesUtc is { } a && a < d)
            {
                problems.Add($"Arrival is before departure ({s.From.Display}).");
            }
        }

        if (Type == DocumentType.BoardingPass && BoardingPass is null && Segments.Count == 0)
        {
            problems.Add("A boarding pass needs a flight.");
        }

        return problems;
    }
}
