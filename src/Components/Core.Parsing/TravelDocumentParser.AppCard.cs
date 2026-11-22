using System.Text.RegularExpressions;
using TravelVault.Core.Documents;

namespace TravelVault.Core.Parsing;

/// <summary>
/// Ticket cards as rail apps show them (RailOne, IRCTC Rail Connect and booking-app screenshots). Nothing is labelled:
/// the meaning is in the placement.
/// <code>
///   20704                                   PNR
///   KCG VANDE BHARAT                        4245374521
///   14:45 Mon, 5 Oct 26   — 4h:38m —   Mon, 5 Oct 26 19:23
///   YESVANTPUR JN.                          KURNOOL CITY
///   YPR                                     KRNT
///   1 Adult, 0 Child | CC | GENERAL | YESVANTPUR...
///   NAME SURNAME        Booking Status   Current Status
///   Male | 73           CNF/C2/17/NC     CNF/C2/17/NC
/// </code>
/// The anchor is the row of two station codes (left = from, right = to); names sit above it, times above them, and the
/// train number above its name.
/// </summary>
public static partial class TravelDocumentParser
{
    internal sealed record AppCard(Place From, Place To, DateTimeMention? Departs, DateTimeMention? Arrives, string? TrainNumber, string? TrainName);

    internal static AppCard? RailAppCard(TextLayout layout, DateOnly reference)
    {
        var rows = layout.Rows();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Cells.Count is < 2 or > 3)
            {
                continue;
            }

            var left = row.Cells[0];
            var right = row.Cells[^1];
            if (!BareCode().IsMatch(left.Text) || !BareCode().IsMatch(right.Text) || left.Text == right.Text || NotAirports.Contains(left.Text) || NotAirports.Contains(right.Text))
            {
                continue;
            }

            var mid = (left.Right + right.Left) / 2;
            var above = (int k) => k >= 0 && rows[k].Page == row.Page ? rows[k] : null;

            // Station names on the row above, one on each side.
            string? fromName = null, toName = null;
            var next = i - 1;
            if (above(next) is { } names && Side(names, mid, true) is { } ln && Side(names, mid, false) is { } rn
                && StationName().IsMatch(ln) && StationName().IsMatch(rn))
            {
                (fromName, toName) = (ln, rn);
                next--;
            }

            // Times and dates above the names (up to three rows: big times and small dates may sit on separate rows).
            string leftWhen = "", rightWhen = "";
            TimeSpan? duration = null;
            for (var k = next; k >= next - 2 && above(k) is { } when; k--)
            {
                if (Duration().Match(when.Text) is { Success: true } d)
                {
                    duration = new TimeSpan(int.Parse(d.Groups["h"].Value), int.Parse(d.Groups["m"].Value), 0);
                }

                var l = Side(when, mid, true, skipDuration: true) ?? "";
                var r = Side(when, mid, false, skipDuration: true) ?? "";
                if (!TimeInText().IsMatch(l + " " + r) && !TextFacts.Analyze(l + "\n" + r, reference).DateTimes.Any())
                {
                    if (leftWhen.Length > 0 || rightWhen.Length > 0)
                    {
                        next = k;
                        break;
                    }

                    continue;
                }

                leftWhen = (l + " " + leftWhen).Trim();
                rightWhen = (r + " " + rightWhen).Trim();
                next = k - 1;
            }

            var departs = When(leftWhen, reference);
            var arrives = When(rightWhen, reference);
            if (duration is { } span)
            {
                // A missing or unreadable time is worked out from the other end and the journey length. A date without
                // its time on that side is likely misread too (OCR turns "5 Oct 26" into "S Oct 26" = 26 Oct), so the
                // complete end wins.
                if (departs?.Time is null && arrives is { Time: not null })
                {
                    var start = arrives.ToDateTime() - span;
                    departs = new DateTimeMention(-1, -1, DateOnly.FromDateTime(start), TimeOnly.FromDateTime(start), false);
                }
                else if (arrives?.Time is null && departs is { Time: not null })
                {
                    var end = departs.ToDateTime() + span;
                    arrives = new DateTimeMention(-1, -1, DateOnly.FromDateTime(end), TimeOnly.FromDateTime(end), false);
                }
            }

            // Train number with its name on the row below, above the times.
            string? number = null, trainName = null;
            for (var k = next; k >= next - 3 && above(k) is { } candidate; k--)
            {
                var cell = candidate.Cells.FirstOrDefault(c => TrainNumberOnly().IsMatch(c.Text));
                if (cell is null)
                {
                    continue;
                }

                number = cell.Text;
                if (k + 1 < rows.Count && rows[k + 1].Cells.FirstOrDefault(c => c.Left < cell.Right && c.Right > cell.Left - cell.Words[0].CharWidth * 2) is { } below
                    && below.Text.Any(char.IsLetter) && !below.Text.Any(char.IsDigit))
                {
                    trainName = First(below.Text, 40);
                }

                break;
            }

            var from = StationPlace(fromName is null ? left.Text : $"{fromName} ({left.Text})", indianRail: true);
            var to = StationPlace(toName is null ? right.Text : $"{toName} ({right.Text})", indianRail: true);
            return new AppCard(from, to, departs, arrives, number, trainName);
        }

        return null;

        static string? Side(LayoutRow row, double mid, bool leftSide, bool skipDuration = false)
        {
            var text = string.Join(' ', row.Cells
                .Where(c => leftSide ? c.CenterX < mid : c.CenterX > mid)
                .Where(c => !skipDuration || !Duration().IsMatch(c.Text))
                .Select(c => c.Text)).Trim();
            return text.Length > 0 ? text : null;
        }

        static DateTimeMention? When(string text, DateOnly reference)
        {
            if (text.Length == 0)
            {
                return null;
            }

            var found = TextFacts.Analyze(text, reference).DateTimes.FirstOrDefault();
            var time = TextFacts.FirstTime(text);
            return found is null ? null : found.Time is null && time is not null ? found with { Time = time } : found;
        }
    }

    /// <summary>"1 Adult, 0 Child | CC | GENERAL | YESVANTPUR…": the class code and quota of an app ticket card.</summary>
    internal static (string? Class, string? Quota) RailSummary(TextFacts facts)
    {
        foreach (var line in facts.Lines)
        {
            // OCR often reads the "|" separators as "I" or "l".
            var parts = Regex.Split(line, @"\s+[|Il1]\s+|\s*\|\s*").Select(p => p.Trim()).ToList();
            if (parts.Count < 2)
            {
                continue;
            }

            var cls = parts.Select(p => RailClassCode().Match(p)).FirstOrDefault(m => m.Success)?.Value.ToUpperInvariant();
            var quota = parts.FirstOrDefault(p => RailQuota().IsMatch(p));
            if (cls is not null)
            {
                return (cls, quota is null ? null : TitleCase(quota));
            }
        }

        return (null, null);
    }

    /// <summary>"CC" → "Chair car (CC)". Unknown codes are kept as printed.</summary>
    internal static string DescribeRailClass(string code) => code.ToUpperInvariant() switch
    {
        "1A" => "First AC (1A)",
        "2A" => "AC 2 tier (2A)",
        "3A" => "AC 3 tier (3A)",
        "3E" => "AC 3 economy (3E)",
        "SL" => "Sleeper (SL)",
        "CC" => "Chair car (CC)",
        "EC" => "Executive chair car (EC)",
        "EA" => "Executive anubhuti (EA)",
        "FC" => "First class (FC)",
        "2S" => "Second sitting (2S)",
        var other => other,
    };

    /// <summary>
    /// Passengers of an app ticket card: a status ("CNF/C2/17/NC") in a row whose first cell is the name, or whose
    /// name is on the row above ("NAME SURNAME" over "Male | 73   CNF/…   CNF/…").
    /// </summary>
    internal static List<RailPassenger> LayoutRailPassengers(TextLayout layout)
    {
        var rows = layout.Rows();
        var list = new List<RailPassenger>();
        for (var i = 0; i < rows.Count; i++)
        {
            var statuses = rows[i].Cells.Select(c => Regex.Replace(c.Text, @"\s+", "")).Where(t => RailStatusOnly().IsMatch(t)).ToList();
            if (statuses.Count == 0)
            {
                continue;
            }

            var name = TextFacts.CleanName(rows[i].Cells[0].Text);
            if (name is null && i > 0 && rows[i - 1].Page == rows[i].Page)
            {
                name = TextFacts.CleanName(rows[i - 1].Cells[0].Text);
            }

            if (name is null)
            {
                continue;
            }

            var current = statuses[^1];
            var (seat, text) = DescribeRailStatus(current);
            list.Add(new RailPassenger(name, current, seat, text));
        }

        return list.DistinctBy(p => p.Name.ToUpperInvariant()).ToList();
    }

    internal sealed record FlightCard(Place From, Place To, DateTimeMention? Departs, DateTimeMention? Arrives, string? CarrierCode, string? Number);

    /// <summary>
    /// Flight legs printed as cards (MakeMyTrip, Goibibo, airline e-tickets): each leg has the departure "BLR 04:35 hrs"
    /// on the left and the arrival "07:00 hrs AUH" on the right, the date under each, and the flight number further left.
    /// <code>
    ///   Etihad Airways   Bengaluru         ··· 03 h 55 m ···       Abu Dhabi
    ///   EY 233           BLR 04:35 hrs                       07:00 hrs AUH
    ///                    Wed, Oct 7                             Wed, Oct 7
    /// </code>
    /// A wrapped city name ("London - Heathrow / Apt") can push one side a row up or down, so the two ends are paired by
    /// nearness (within two rows) and side (left = departure), not by sharing a row.
    /// </summary>
    internal static List<FlightCard> FlightCards(TextLayout layout, DateOnly reference)
    {
        var rows = layout.Rows();
        var directory = AirportDirectory.Instance;
        var ends = new List<(int Row, LayoutCell Cell, Airport Airport, TimeOnly Time)>();
        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var cell in rows[i].Cells)
            {
                var m = CodeAndTime().Match(cell.Text);
                var code = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;
                if (m.Success && m.Groups["a"].Success != m.Groups["b"].Success && !NotAirports.Contains(code)
                    && directory.Find(code) is { } airport && TextFacts.FirstTime(m.Groups["t"].Value) is { } time)
                {
                    ends.Add((i, cell, airport, time));
                }
            }
        }

        var cards = new List<FlightCard>();
        var used = new bool[ends.Count];
        for (var a = 0; a < ends.Count; a++)
        {
            if (used[a])
            {
                continue;
            }

            var first = ends[a];
            var partner = Enumerable.Range(0, ends.Count)
                .Where(b => b != a && !used[b] && rows[ends[b].Row].Page == rows[first.Row].Page && Math.Abs(ends[b].Row - first.Row) <= 2
                    && ends[b].Airport.Code != first.Airport.Code
                    && Math.Abs(ends[b].Cell.CenterX - first.Cell.CenterX) > Math.Max(first.Cell.Right - first.Cell.Left, ends[b].Cell.Right - ends[b].Cell.Left))
                .OrderBy(b => Math.Abs(ends[b].Row - first.Row))
                .Cast<int?>()
                .FirstOrDefault();
            if (partner is not { } p)
            {
                continue;
            }

            used[a] = used[p] = true;
            var (dep, arr) = first.Cell.CenterX < ends[p].Cell.CenterX ? (first, ends[p]) : (ends[p], first);
            var depDate = DateUnder(dep.Row, dep.Cell);
            var arrDate = DateUnder(arr.Row, arr.Cell) ?? depDate;
            DateTimeMention? departs = depDate is { } dd ? new DateTimeMention(-1, -1, dd, dep.Time, false) : null;
            DateTimeMention? arrives = null;
            if (arrDate is { } ad)
            {
                var end = ad.ToDateTime(arr.Time);
                if (departs is not null && end <= departs.ToDateTime() && DateUnder(arr.Row, arr.Cell) is null)
                {
                    end = end.AddDays(1); // only a time printed, earlier than departure: the next day
                }

                arrives = new DateTimeMention(-1, -1, DateOnly.FromDateTime(end), arr.Time, false);
            }

            var (carrier, number) = FlightNumberLeftOf(dep.Row, dep.Cell);
            cards.Add(new FlightCard(dep.Airport.ToPlace(), arr.Airport.ToPlace(), departs, arrives, carrier, number));
        }

        return cards;

        // The date printed under (or in) an end's cell: the nearest of the next three rows in the same column.
        DateOnly? DateUnder(int row, LayoutCell anchor)
        {
            if (TextFacts.Analyze(anchor.Text, reference).DateTimes.FirstOrDefault() is { } own)
            {
                return own.Date;
            }

            var slack = anchor.Words[0].CharWidth * 2;
            for (var r = row + 1; r <= row + 3 && r < rows.Count && rows[r].Page == rows[row].Page; r++)
            {
                foreach (var cell in rows[r].Cells.Where(c => c.Left < anchor.Right + slack && c.Right > anchor.Left - slack))
                {
                    if (TextFacts.Analyze(cell.Text, reference).DateTimes.FirstOrDefault() is { } found)
                    {
                        return found.Date;
                    }
                }
            }

            return null;
        }

        // "EY 233" in a cell left of the departure, on the same row or up to two rows away.
        (string? Code, string? Number) FlightNumberLeftOf(int row, LayoutCell anchor)
        {
            foreach (var r in new[] { row, row - 1, row + 1, row - 2, row + 2 })
            {
                if (r < 0 || r >= rows.Count || rows[r].Page != rows[row].Page)
                {
                    continue;
                }

                foreach (var cell in rows[r].Cells.Where(c => c.Right <= anchor.Left))
                {
                    var m = FlightNumber().Match(cell.Text);
                    if (m.Success && m.Groups[1].Value.ToUpperInvariant() is var code && Airlines.IsKnown(code) && code.Any(char.IsLetter))
                    {
                        return (code, m.Groups[2].Value.TrimStart('0'));
                    }
                }
            }

            return (null, null);
        }
    }

    [GeneratedRegex(@"^(?:(?<a>[A-Z]{3})\s+)?(?<t>(?:[01]?\d|2[0-3])[:.][0-5]\d)(?:\s*hrs?)?(?:\s+(?<b>[A-Z]{3}))?$")]
    private static partial Regex CodeAndTime();

    [GeneratedRegex(@"(?<h>\d{1,2})\s*h(?:rs?)?\s*[:.]?\s*(?<m>\d{1,2})\s*m", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    [GeneratedRegex(@"^[A-Z][A-Z .()'-]{2,40}$")]
    private static partial Regex StationName();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex TrainNumberOnly();

    [GeneratedRegex(@"^(?:1A|2A|3A|3E|SL|CC|EC|EA|FC|2S)$", RegexOptions.IgnoreCase)]
    private static partial Regex RailClassCode();

    [GeneratedRegex(@"^(?:GENERAL|GN|TATKAL|TQ|PREMIUM\s+TATKAL|PT|LADIES|LD|SENIOR\s+CITIZEN|SS|LOWER\s+BERTH|DIVYAANG|HANDICAPPED|HP|DEFENCE|FOREIGN\s+TOURIST|YUVA)$", RegexOptions.IgnoreCase)]
    private static partial Regex RailQuota();

    [GeneratedRegex(@"^(?:CNF|RAC|CAN|NOSB|[A-Z]{0,4}WL)(?:/[A-Z0-9]+)*$")]
    private static partial Regex RailStatusOnly();
}
