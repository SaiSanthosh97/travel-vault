using TravelVault.Core.Documents;

namespace TravelVault.Core.Trips;

public sealed record TripSuggestion(string Name, IReadOnlyList<string> DocumentIds, DateTimeOffset StartsUtc, DateTimeOffset EndsUtc)
{
    /// <summary>Stable key so a dismissed suggestion isn't shown again.</summary>
    public string Key => string.Join(",", DocumentIds.Order(StringComparer.Ordinal));
}

/// <summary>
/// Suggests trips by chaining documents in time order, for overlapping travelers:
/// a journey continues when the next item starts where the previous one ended (same city) within 4 days,
/// or is a stay/event in the current city. The chain closes when it returns to where it started.
/// Only suggestions with 2+ documents are offered; the user confirms, renames or dismisses them.
/// </summary>
public static class TripGrouper
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromDays(4);

    private sealed class Chain
    {
        public required Place Origin { get; init; }

        public Place? Location { get; set; }

        public DateTimeOffset Start { get; init; }

        public DateTimeOffset End { get; set; }

        public HashSet<string> Travelers { get; } = [];

        public List<TravelDocument> Documents { get; } = [];

        public bool Closed { get; set; }
    }

    public static IReadOnlyList<TripSuggestion> Suggest(IEnumerable<TravelDocument> documents)
    {
        var candidates = documents
            .Where(d => !d.IsDeleted && d.TripId is null && d.Type is not (DocumentType.BoardingPass or DocumentType.Visa or DocumentType.Passport or DocumentType.Other))
            .Where(d => d.StartsUtc is not null && d.Segments.Count > 0)
            .OrderBy(d => d.StartsUtc)
            .ToList();

        var chains = new List<Chain>();
        foreach (var doc in candidates)
        {
            var first = doc.Segments[0];
            var last = doc.Segments[^1];
            var start = doc.StartsUtc!.Value;
            var end = doc.EndsUtc ?? start;
            var travelers = doc.AllTravelerIds();
            var isPlace = first.Mode is TravelMode.Stay or TravelMode.Event;

            var chain = chains.FirstOrDefault(c =>
                !c.Closed
                && c.Travelers.Overlaps(travelers)
                && start - c.End <= MaxGap
                && start >= c.Start
                && (first.From.SameCityAs(c.Location) || (isPlace && (first.From.City is null || first.From.SameCityAs(c.Location)))));

            if (chain is null)
            {
                if (isPlace)
                {
                    continue; // a stay alone doesn't start a trip suggestion; it can join one
                }

                chain = new Chain { Origin = first.From, Start = start };
                chains.Add(chain);
            }

            chain.Documents.Add(doc);
            chain.Travelers.UnionWith(travelers);
            chain.End = end > chain.End ? end : chain.End;
            if (!isPlace)
            {
                chain.Location = last.To ?? last.From;
                if (chain.Documents.Count > 1 && chain.Location.SameCityAs(chain.Origin))
                {
                    chain.Closed = true; // back home
                }
            }
        }

        return chains
            .Where(c => c.Documents.Count >= 2)
            .Select(c => new TripSuggestion(Name(c), c.Documents.Select(d => d.Id).ToList(), c.Start, c.End))
            .ToList();
    }

    private static string Name(Chain chain)
    {
        // The main destination: the first place reached that isn't the origin.
        var destination = chain.Documents
            .SelectMany(d => d.Segments)
            .Select(s => s.To ?? s.From)
            .FirstOrDefault(p => !p.SameCityAs(chain.Origin));
        var city = destination?.City ?? destination?.Name ?? "Trip";
        var first = chain.Documents[0].Segments[0].Departs?.Local ?? chain.Start.UtcDateTime;
        var lastSegment = chain.Documents[^1].Segments[^1];
        var last = (lastSegment.Arrives ?? lastSegment.Departs)?.Local ?? chain.End.UtcDateTime;
        return $"{city}, {TripStatuses.DateRange(first, last)}";
    }
}
