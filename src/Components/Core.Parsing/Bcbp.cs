using System.Globalization;

namespace TravelVault.Core.Parsing;

public sealed record BcbpLeg(
    string Pnr,
    string From,
    string To,
    string Carrier,
    string FlightNumber,
    DateOnly? Date,
    char Compartment,
    string Seat,
    string CheckInSequence);

public sealed record BoardingPassData(string PassengerName, bool ElectronicTicket, IReadOnlyList<BcbpLeg> Legs);

/// <summary>
/// Decodes the IATA Bar Coded Boarding Pass (BCBP, Resolution 792) printed as PDF417 / Aztec / QR on almost every
/// boarding pass worldwide. It is a fixed-width text format, so it's decoded exactly, without guessing:
/// <code>
/// M1SHARMA/RAHULMR       EX7KQ2P BLRDEL6E 2345 346Y012A0045 100
/// │││                   ││      │  │  │  │    │  ││   │    │└┬┘
/// ││└ name (20)         │└ PNR(7)  to carrier flight date │seat seq  status, size of extra data (hex)
/// │└ number of legs     └ e-ticket                      day-of-year, compartment
/// └ format code
/// </code>
/// The year is not in the barcode (only the day of the year), so it's inferred from a reference date
/// (when the pass was imported): the closest matching date wins.
/// </summary>
public static class BcbpDecoder
{
    private const int FirstLegMandatoryLength = 60;
    private const int RepeatedLegLength = 37;

    public static bool LooksLikeBcbp(string? text) =>
        text is { Length: >= FirstLegMandatoryLength } && text[0] == 'M' && text[1] is >= '1' and <= '4';

    public static BoardingPassData? Decode(string? text, DateOnly reference)
    {
        if (!LooksLikeBcbp(text))
        {
            return null;
        }

        var legCount = text![1] - '0';
        var name = text.Substring(2, 20).Trim();
        var electronic = text[22] == 'E';
        var legs = new List<BcbpLeg>();
        var position = 23; // first leg's PNR starts here

        for (var i = 0; i < legCount; i++)
        {
            if (position + RepeatedLegLength > text.Length)
            {
                break; // truncated barcode: keep the legs we have
            }

            var leg = ReadLeg(text, position, reference);
            if (leg is null)
            {
                return legs.Count > 0 ? new BoardingPassData(name, electronic, legs) : null;
            }

            legs.Add(leg);
            if (!int.TryParse(text.AsSpan(position + 35, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var variableSize))
            {
                break;
            }

            position += RepeatedLegLength + variableSize;
        }

        return legs.Count == 0 || name.Length == 0 ? null : new BoardingPassData(name, electronic, legs);
    }

    private static BcbpLeg? ReadLeg(string text, int p, DateOnly reference)
    {
        var from = text.Substring(p + 7, 3).Trim();
        var to = text.Substring(p + 10, 3).Trim();
        if (!IsAirportCode(from) || !IsAirportCode(to))
        {
            return null;
        }

        var carrier = text.Substring(p + 13, 3).Trim();
        var flight = text.Substring(p + 16, 5).Trim().TrimStart('0');
        var julian = text.Substring(p + 21, 3).Trim();
        var seat = text.Substring(p + 25, 4).Trim().TrimStart('0');
        var sequence = text.Substring(p + 29, 5).Trim().TrimStart('0');
        return new BcbpLeg(
            Pnr: text.Substring(p, 7).Trim(),
            From: from,
            To: to,
            Carrier: carrier,
            FlightNumber: flight.Length == 0 ? "0" : flight,
            Date: int.TryParse(julian, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ? DayOfYearNear(day, reference) : null,
            Compartment: text[p + 24],
            Seat: seat,
            CheckInSequence: sequence);
    }

    /// <summary>Day-of-year → the calendar date nearest to the reference (previous, same or next year).</summary>
    internal static DateOnly? DayOfYearNear(int dayOfYear, DateOnly reference)
    {
        if (dayOfYear is < 1 or > 366)
        {
            return null;
        }

        DateOnly? best = null;
        foreach (var year in new[] { reference.Year - 1, reference.Year, reference.Year + 1 })
        {
            if (dayOfYear == 366 && !DateTime.IsLeapYear(year))
            {
                continue;
            }

            var candidate = new DateOnly(year, 1, 1).AddDays(dayOfYear - 1);
            if (best is null || Math.Abs(candidate.DayNumber - reference.DayNumber) < Math.Abs(best.Value.DayNumber - reference.DayNumber))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsAirportCode(string code) => code.Length == 3 && code.All(char.IsAsciiLetterUpper);
}
