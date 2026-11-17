using TravelVault.Core.Documents;

namespace TravelVault.Core.Duplicates;

public enum DuplicateStrength
{
    /// <summary>Same booking reference, same people, same day: almost certainly an updated or re-sent copy. Offer merge.</summary>
    Strong,

    /// <summary>Same route, day and someone in common, but no reference to compare: show a dismissible hint.</summary>
    Weak,
}

public sealed record DuplicateCandidate(TravelDocument Existing, DuplicateStrength Strength, string Reason);

/// <summary>
/// Fuzzy duplicate rules (exact duplicates — identical bytes — are caught earlier by file hash).
/// Boarding passes are never duplicates of tickets: they're a different document about the same flight
/// and get linked instead.
/// </summary>
public static class DuplicateDetector
{
    public static IReadOnlyList<DuplicateCandidate> Find(TravelDocument incoming, IEnumerable<TravelDocument> existing)
    {
        var results = new List<DuplicateCandidate>();
        foreach (var other in existing)
        {
            if (other.Id == incoming.Id || other.IsDeleted || other.FamilyId != incoming.FamilyId || other.Type != incoming.Type)
            {
                continue;
            }

            var sameReference = BookingCodes.Same(incoming.Pnr, other.Pnr)
                || BookingCodes.Same(incoming.BookingReference, other.BookingReference)
                || BookingCodes.Same(incoming.Pnr, other.BookingReference)
                || BookingCodes.Same(incoming.BookingReference, other.Pnr);
            var people = SharedTravelers(incoming, other);
            var sameDay = SameDayOrOverlap(incoming, other);

            if (incoming.Type is DocumentType.Visa or DocumentType.Passport)
            {
                if (incoming.Identity?.Number is { Length: > 0 } number && number == other.Identity?.Number)
                {
                    results.Add(new DuplicateCandidate(other, DuplicateStrength.Strong, "Same document number"));
                }

                continue;
            }

            if (sameReference && people != false && sameDay != false)
            {
                results.Add(new DuplicateCandidate(other, DuplicateStrength.Strong, "Same booking reference, travelers and date"));
            }
            else if (!HasReference(incoming) || !HasReference(other))
            {
                if (people == true && sameDay == true && SameRoute(incoming, other))
                {
                    results.Add(new DuplicateCandidate(other, DuplicateStrength.Weak, "Same route, date and traveler"));
                }
            }
        }

        return results.OrderBy(r => r.Strength).ToList();
    }

    private static bool HasReference(TravelDocument d) => BookingCodes.Normalize(d.Pnr) is not null || BookingCodes.Normalize(d.BookingReference) is not null;

    /// <returns>null when either side has no travelers to compare.</returns>
    private static bool? SharedTravelers(TravelDocument a, TravelDocument b)
    {
        var x = a.AllTravelerIds();
        var y = b.AllTravelerIds();
        return x.Count == 0 || y.Count == 0 ? null : x.Intersect(y).Any();
    }

    /// <returns>null when either side has no dates.</returns>
    private static bool? SameDayOrOverlap(TravelDocument a, TravelDocument b)
    {
        if (a.StartsUtc is not { } aStart || b.StartsUtc is not { } bStart)
        {
            return null;
        }

        if (aStart.UtcDateTime.Date == bStart.UtcDateTime.Date)
        {
            return true;
        }

        // Stays and multi-day documents: overlapping periods count as the same booking window.
        var aEnd = a.EndsUtc ?? aStart;
        var bEnd = b.EndsUtc ?? bStart;
        return aStart <= bEnd && bStart <= aEnd && (a.Type == DocumentType.Hotel);
    }

    private static bool SameRoute(TravelDocument a, TravelDocument b)
    {
        var x = a.Segments.FirstOrDefault();
        var y = b.Segments.FirstOrDefault();
        return x is not null && y is not null && x.From.SameCityAs(y.From) && (x.To is null ? y.To is null : x.To.SameCityAs(y.To));
    }
}
