using TravelVault.Core.Documents;

namespace TravelVault.Core.Trips;

/// <summary>
/// Links a boarding pass to the flight segment it is for, and finds who still lacks one.
/// Match: same airline PNR (or same flight number) AND same route AND same date (±1 day for overnight / time-zone
/// differences), and the boarding pass's traveler must be on that ticket.
/// </summary>
public static class BoardingPassLinker
{
    public static BoardingPassLink? FindTarget(TravelDocument boardingPass, IEnumerable<TravelDocument> documents)
    {
        if (boardingPass.Segments.Count == 0)
        {
            return null;
        }

        var leg = boardingPass.Segments[0];
        var traveler = boardingPass.Passengers.FirstOrDefault()?.TravelerId;
        foreach (var ticket in documents.Where(d => !d.IsDeleted && d.Type == DocumentType.Flight && d.Id != boardingPass.Id))
        {
            var samePnr = BookingCodes.Same(ticket.Pnr, boardingPass.Pnr) || BookingCodes.Same(ticket.BookingReference, boardingPass.Pnr);
            foreach (var segment in ticket.Segments.Where(s => s.Mode == TravelMode.Flight))
            {
                var sameRoute = string.Equals(segment.From.Code, leg.From.Code, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(segment.To?.Code, leg.To?.Code, StringComparison.OrdinalIgnoreCase);
                var sameFlight = Normalize(segment.ServiceNumber) is { } a && a == Normalize(leg.ServiceNumber);
                var sameDate = segment.Departs is { } x && leg.Departs is { } y && Math.Abs((x.Local.Date - y.Local.Date).TotalDays) <= 1;
                if (sameRoute && sameDate && (samePnr || sameFlight))
                {
                    var onTicket = traveler is not null && ticket.AllTravelerIds().Contains(traveler);
                    var who = onTicket ? traveler! : ticket.Passengers.Count == 1 ? ticket.Passengers[0].TravelerId : traveler;
                    if (who is not null)
                    {
                        return new BoardingPassLink(ticket.Id, segment.Id, who);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Travelers on a flight segment who have no linked boarding pass yet.</summary>
    public static IReadOnlyList<string> MissingFor(TravelDocument ticket, Segment segment, IEnumerable<TravelDocument> documents)
    {
        if (segment.Mode != TravelMode.Flight)
        {
            return [];
        }

        var travelers = segment.TravelerIds.Count > 0 ? segment.TravelerIds : ticket.Passengers.Select(p => p.TravelerId).ToList();
        var covered = documents
            .Where(d => !d.IsDeleted && d.Type == DocumentType.BoardingPass && d.BoardingPass is { } link && link.TicketDocumentId == ticket.Id && link.SegmentId == segment.Id)
            .Select(d => d.BoardingPass!.TravelerId)
            .ToHashSet();
        return travelers.Where(t => !covered.Contains(t)).ToList();
    }

    /// <summary>Boarding passes linked to a ticket, for showing "ticket + boarding pass" together.</summary>
    public static IReadOnlyList<TravelDocument> LinkedTo(TravelDocument ticket, IEnumerable<TravelDocument> documents) =>
        documents.Where(d => !d.IsDeleted && d.BoardingPass?.TicketDocumentId == ticket.Id).ToList();

    private static string? Normalize(string? flight) =>
        flight is null ? null : new string(flight.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant().TrimStart('0');
}
