using System.Globalization;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;

namespace TravelVault.Core.Trips;

public enum TripStatus
{
    Upcoming,
    Ongoing,
    Completed,
}

/// <summary>
/// A named group of documents (e.g. "Delhi, 12–15 Dec"). Membership is stored on the documents
/// (<see cref="TravelDocument.TripId"/>), not here, so there is one source of truth that can't disagree after sync.
/// Dates and status are derived from the member documents.
/// </summary>
public sealed class Trip : IVersioned
{
    public required string Id { get; init; }

    public required string FamilyId { get; init; }

    public required string Name { get; set; }

    public string? Notes { get; set; }

    /// <summary>The user can extend the end (e.g. a stay with no return ticket yet).</summary>
    public DateTimeOffset? EndsOverrideUtc { get; set; }

    public HybridTimestamp Version { get; set; }

    public string UpdatedBy { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public static Trip Create(string familyId, string name) => new() { Id = Ids.New(), FamilyId = familyId, Name = name.Trim() };
}

/// <summary>A trip with its documents, as shown on screen.</summary>
public sealed record TripView(Trip Trip, IReadOnlyList<TravelDocument> Documents)
{
    /// <summary>A trip that ended more than 6 hours ago is completed (allows for delays and the trip home).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(6);

    public DateTimeOffset? StartsUtc => Documents.Select(d => d.StartsUtc).Where(t => t is not null).Min();

    public DateTimeOffset? EndsUtc => Trip.EndsOverrideUtc ?? Documents.Select(d => d.EndsUtc ?? d.StartsUtc).Where(t => t is not null).Max();

    public IReadOnlyList<string> TravelerIds => Documents.SelectMany(d => d.AllTravelerIds()).Distinct().ToList();

    public TripStatus Status(DateTimeOffset now) => TripStatuses.Of(StartsUtc, EndsUtc, now);
}

public static class TripStatuses
{
    public static TripStatus Of(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        if (start is null || now < start)
        {
            return TripStatus.Upcoming;
        }

        return now <= (end ?? start) + TripView.Grace ? TripStatus.Ongoing : TripStatus.Completed;
    }

    /// <summary>"12–15 Dec 2026", "28 Dec 2026 – 3 Jan 2027".</summary>
    public static string DateRange(DateTime start, DateTime end)
    {
        var c = CultureInfo.InvariantCulture;
        if (start.Date == end.Date)
        {
            return start.ToString("d MMM yyyy", c);
        }

        if (start.Year == end.Year && start.Month == end.Month)
        {
            return $"{start.Day}–{end.ToString("d MMM yyyy", c)}";
        }

        return start.Year == end.Year
            ? $"{start.ToString("d MMM", c)} – {end.ToString("d MMM yyyy", c)}"
            : $"{start.ToString("d MMM yyyy", c)} – {end.ToString("d MMM yyyy", c)}";
    }
}
