using System.Globalization;
using System.Text.RegularExpressions;

namespace TravelVault.Core.Parsing;

/// <summary>A date (and maybe a time) found in the text, with where it was found.</summary>
public sealed record DateTimeMention(int Line, int Index, DateOnly Date, TimeOnly? Time, bool Ambiguous)
{
    public DateTime ToDateTime() => Date.ToDateTime(Time ?? TimeOnly.MinValue);
}

/// <summary>
/// Everything the rule-based parsers need from raw ticket text: lines, dates with their times, values next to
/// labels ("PNR: X7KQ2P"), booking codes and person names. Pure text logic, so it works the same for PDF text
/// and OCR output, and is unit-tested with plain strings.
/// </summary>
public sealed partial class TextFacts
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["sept"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
    };

    /// <summary>Words that look like codes but never are (avoids "CONFIRMED" as a PNR).</summary>
    private static readonly HashSet<string> NotCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NUMBER", "STATUS", "CONFIRMED", "BOOKING", "BOOKED", "DETAILS", "TICKET", "PASSENGER", "REFERENCE", "PENDING",
        "DEPARTURE", "ARRIVAL", "ECONOMY", "BUSINESS", "AMOUNT", "TOTAL", "BOARDING", "TERMINAL", "BAGGAGE", "SEATNO",
        "CHECKIN", "CHECKOUT", "INCLUDED", "PAYMENT", "INVOICE", "ISSUED", "ADULT", "CHILD", "GUEST", "ROOMS",
    };

    private TextFacts(string text, DateOnly reference, IReadOnlyList<FormField>? fields)
    {
        Fields = fields ?? [];
        Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Lines = Text.Split('\n').Select(l => l.Trim()).ToList();
        Reference = reference;
        DateTimes = FindDateTimes();
    }

    public string Text { get; }

    public IReadOnlyList<string> Lines { get; }

    /// <summary>The import date: used to infer missing years.</summary>
    public DateOnly Reference { get; }

    /// <summary>All dates in reading order, each paired with a time when one is printed next to it.</summary>
    public IReadOnlyList<DateTimeMention> DateTimes { get; }

    /// <summary>Label/value pairs read from the page layout (see <see cref="FormReader"/>). Checked before the plain text.</summary>
    public IReadOnlyList<FormField> Fields { get; }

    public static TextFacts Analyze(string text, DateOnly reference, IReadOnlyList<FormField>? fields = null) => new(text ?? "", reference, fields);

    /// <summary>The value of the first form field whose label matches the pattern (a regex fragment), or null.</summary>
    public string? FieldValue(string label) => FieldsMatching(label).Select(f => f.Value).FirstOrDefault();

    private IEnumerable<FormField> FieldsMatching(string label)
    {
        var regex = new Regex(@"(?<![A-Za-z])(?:" + label + @")(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Fields.Where(f => f.Value.Length > 0 && regex.IsMatch(f.Label));
    }

    public bool Has(string pattern) => Regex.IsMatch(Text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public int Count(string pattern) => Regex.Matches(Text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;

    /// <summary>
    /// The text after a label, on the same line ("PNR: X7KQ2P") or, if the label ends its line, the next non-empty line.
    /// Labels are regex fragments matched as whole words, tried in order.
    /// </summary>
    public string? LabelValue(params string[] labels)
    {
        foreach (var label in labels)
        {
            if (FieldValue(label) is { } field)
            {
                return field;
            }

            var regex = new Regex(@"(?<![A-Za-z])(?:" + label + @")(?![A-Za-z])\s*(?:no\.?|number|#)?\s*[:\-–#.]?\s*(?<v>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (var i = 0; i < Lines.Count; i++)
            {
                var m = regex.Match(Lines[i]);
                if (!m.Success)
                {
                    continue;
                }

                var value = m.Groups["v"].Value.Trim();
                if (value.Length > 0)
                {
                    return value;
                }

                // "Booking ID:" alone on its line → the value is on the next line. But a word that merely ends a
                // sentence ("The Grand Residency Hotel") is not a label, so only accept labels that start the line.
                if (m.Index > 0)
                {
                    continue;
                }

                var next = NextNonEmpty(i);
                if (next is not null)
                {
                    return next;
                }
            }
        }

        return null;
    }

    /// <summary>A booking code after a label: the first 5–14 character token with letters/digits (and at least one digit unless 6 letters).</summary>
    public string? LabelCode(params string[] labels)
    {
        foreach (var label in labels)
        {
            var value = LabelValue(label);
            if (value is not null && FirstCode(value) is { } code)
            {
                return code;
            }
        }

        return null;
    }

    public static string? FirstCode(string value)
    {
        foreach (var raw in value.Split([' ', ',', ';', '|', '/', '\t', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.', ':', '-', '#').ToUpperInvariant();
            if (DottedNumber().IsMatch(token))
            {
                token = token.Replace(".", "", StringComparison.Ordinal); // Booking.com: "4128.553.911"
            }

            if (token.Length is < 5 or > 14 || !token.All(char.IsAsciiLetterOrDigit) || NotCodes.Contains(token))
            {
                continue;
            }

            var hasDigit = token.Any(char.IsAsciiDigit);
            if (hasDigit || token.Length == 6)
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>Line index of the first line matching the pattern, or -1.</summary>
    public int LineOf(string pattern, int from = 0)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var i = Math.Max(0, from); i < Lines.Count; i++)
        {
            if (regex.IsMatch(Lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The first date-time on or after a line (e.g. the one following a "Check-in" label).</summary>
    /// <remarks>
    /// Several lines may carry the label ("Date of Journey: 20-Dec" and "Departure: 19:20 20-Dec"); a date that has a
    /// time is preferred, because the time is what reminders need.
    /// </remarks>
    public DateTimeMention? DateTimeNear(string labelPattern)
    {
        DateTimeMention? withoutTime = null;
        foreach (var field in FieldsMatching(labelPattern))
        {
            var mention = Analyze(field.Value, Reference).DateTimes.FirstOrDefault();
            if (mention?.Time is not null)
            {
                return mention with { Line = -1 };
            }

            withoutTime ??= mention is null ? null : mention with { Line = -1 };
        }

        for (var line = LineOf(labelPattern); line >= 0; line = LineOf(labelPattern, line + 1))
        {
            var mention = DateTimes.FirstOrDefault(d => d.Line == line) ?? DateTimes.FirstOrDefault(d => d.Line > line && d.Line <= line + 3);
            if (mention?.Time is not null)
            {
                return mention;
            }

            withoutTime ??= mention;
        }

        return withoutTime;
    }

    /// <summary>The first time in a value: "22:00", "9:10 PM", "2210 hrs".</summary>
    public static TimeOnly? FirstTime(string value)
    {
        foreach (var regex in new[] { Time24(), TimeAmPm(), TimeHrs() })
        {
            var m = regex.Match(value);
            if (m.Success && ToTime(m) is { } t)
            {
                return t;
            }
        }

        return null;
    }

    public string? NextNonEmpty(int line)
    {
        for (var j = line + 1; j < Lines.Count; j++)
        {
            if (Lines[j].Length > 0)
            {
                return Lines[j];
            }
        }

        return null;
    }

    // ---------- People ----------

    private static readonly string[] NameNoise =
    [
        "flight", "date", "time", "seat", "class", "from", "to", "pnr", "booking", "ticket", "status", "fare", "total", "terminal",
        "gate", "boarding", "departure", "arrival", "hotel", "room", "check", "train", "coach", "berth", "bus", "venue", "event",
        "amount", "paid", "economy", "business", "baggage", "airport", "station", "email", "phone", "mobile", "address", "type",
        "adult", "child", "infant", "male", "female", "age", "gender", "confirmed", "reference", "number", "name", "details",
        // Ordinary words: a line containing these is a sentence or a heading, not a person.
        "sample", "synthetic", "test", "testing", "thank", "thanks", "please", "your", "our", "this", "the", "with", "and",
        "for", "is", "are", "of", "not", "real", "only", "valid", "visa", "passport", "nationality", "entries", "multiple",
        "confirmation", "electronic", "standard", "premier", "cabin", "main", "allowance", "receipt", "itinerary", "order",
    ];

    /// <summary>
    /// Passenger / guest names: values after name labels, lines listed under a "Passenger(s)" header, and lines that
    /// start with a title (Mr/Mrs/Ms…). Cleaned of extras like "(Adult)", ages and seat numbers.
    /// </summary>
    public IReadOnlyList<string> PersonNames()
    {
        var found = new List<string>();
        foreach (var field in FieldsMatching(@"^(?:passenger|traveller|traveler|guest|lead\s*guest)?\s*names?$|^(?:passenger|traveller|traveler|guest)s?$"))
        {
            if (CleanName(field.Value) is { } name)
            {
                found.Add(name);
            }
        }

        var labelRegex = LabelForNames();
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var label = labelRegex.Match(line);
            if (label.Success)
            {
                var rest = label.Groups["v"].Value;
                if (CleanName(rest) is { } inline)
                {
                    found.Add(inline);
                }

                // A header row: names follow on the next lines until something that isn't a name.
                for (var j = i + 1; j < Lines.Count && j <= i + 9; j++)
                {
                    if (Lines[j].Length == 0)
                    {
                        continue;
                    }

                    if (CleanName(Lines[j]) is { } listed)
                    {
                        found.Add(listed);
                    }
                    else
                    {
                        break;
                    }
                }

                continue;
            }

            if (TitledName().IsMatch(line) && CleanName(line) is { } titled)
            {
                found.Add(titled);
            }
        }

        return found
            .DistinctBy(n => string.Join(' ', n.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Order()))
            .ToList();
    }

    internal static string? CleanName(string raw)
    {
        var s = raw.Trim();
        s = LeadingEnumeration().Replace(s, "");
        s = Regex.Replace(s, @"\(.*?\)", " ");
        s = s.Split([',', '|', '\t', ';'], 2)[0];

        // Stop at the first "column" that isn't part of a name (two+ spaces in table text, a digit, or a noise word).
        s = Regex.Split(s, @"\s{2,}")[0];
        var tokens = new List<string>();
        foreach (var token in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim('.', ':', '-');
            if (t.Length == 0)
            {
                continue;
            }

            if (t.Any(char.IsDigit) || NameNoise.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                break;
            }

            if (!t.All(c => char.IsLetter(c) || c is '\'' or '/' or '-'))
            {
                break;
            }

            tokens.Add(t);
        }

        while (tokens.Count > 0 && tokens[^1].Length == 1 && tokens[^1] is "M" or "F" && tokens.Count > 2)
        {
            tokens.RemoveAt(tokens.Count - 1); // trailing gender letter
        }

        var letters = tokens.Sum(t => t.Count(char.IsLetter));
        var titles = tokens.Count(t => IsTitle(t));
        var nameWords = tokens.Count - titles;
        var slashName = tokens.Any(t => t.Contains('/')) && nameWords >= 1 && letters >= 5; // "SHARMA/ANANYA MS"
        return (nameWords is >= 2 and <= 5 || slashName) && letters >= 4 ? string.Join(' ', tokens) : null;
    }

    private static bool IsTitle(string token) =>
        token.ToUpperInvariant().Trim('.') is "MR" or "MRS" or "MS" or "MISS" or "MSTR" or "MASTER" or "DR" or "SHRI" or "SMT";

    // ---------- Dates & times ----------

    private List<DateTimeMention> FindDateTimes()
    {
        var mentions = new List<DateTimeMention>();
        var lineStart = 0;
        for (var lineIndex = 0; lineIndex < Lines.Count; lineIndex++)
        {
            var line = Lines[lineIndex];
            var used = new bool[line.Length];
            var dates = new List<(int Index, int End, DateOnly Date, bool Ambiguous)>();

            foreach (var (regex, kind) in new[] { (Iso(), 0), (DayMonthName(), 1), (MonthNameDay(), 2), (Numeric(), 3) })
            {
                foreach (Match m in regex.Matches(line))
                {
                    if (Overlaps(used, m.Index, m.Length) || ToDate(m, kind) is not { } d)
                    {
                        continue;
                    }

                    Mark(used, m.Index, m.Length);
                    dates.Add((m.Index, m.Index + m.Length, d.Date, d.Ambiguous));
                }
            }

            var times = new List<(int Index, TimeOnly Time)>();
            foreach (var regex in new[] { Time24(), TimeAmPm(), TimeHrs() })
            {
                foreach (Match m in regex.Matches(line))
                {
                    if (Overlaps(used, m.Index, m.Length) || ToTime(m) is not { } t)
                    {
                        continue;
                    }

                    Mark(used, m.Index, m.Length);
                    times.Add((m.Index, t));
                }
            }

            dates.Sort((a, b) => a.Index.CompareTo(b.Index));
            times.Sort((a, b) => a.Index.CompareTo(b.Index));
            var usedTimes = new HashSet<int>();
            foreach (var d in dates)
            {
                // Nearest unused time on the same line: after the date first, then before it.
                var time = times.Where(t => !usedTimes.Contains(t.Index) && t.Index >= d.End).Select(t => (int?)t.Index).FirstOrDefault()
                    ?? times.Where(t => !usedTimes.Contains(t.Index) && t.Index < d.Index).Select(t => (int?)t.Index).LastOrDefault();
                TimeOnly? value = null;
                if (time is { } ti)
                {
                    usedTimes.Add(ti);
                    value = times.First(t => t.Index == ti).Time;
                }

                mentions.Add(new DateTimeMention(lineIndex, lineStart + d.Index, d.Date, value, d.Ambiguous));
            }

            // A date line without a time, followed by a line that is only a time ("12 Dec 2026" / "06:10").
            if (dates.Count == 1 && times.Count == 0 && mentions.Count > 0 && mentions[^1].Time is null && lineIndex + 1 < Lines.Count)
            {
                var next = Lines[lineIndex + 1];
                var m = Time24().Match(next);
                if (!m.Success)
                {
                    m = TimeAmPm().Match(next);
                }

                if (m.Success && next.Length <= m.Length + 12 && ToTime(m) is { } nt)
                {
                    mentions[^1] = mentions[^1] with { Time = nt };
                }
            }

            lineStart += line.Length + 1;
        }

        return mentions;
    }

    private (DateOnly Date, bool Ambiguous)? ToDate(Match m, int kind)
    {
        try
        {
            switch (kind)
            {
                case 0:
                    return (new DateOnly(Int(m.Groups[1]), Int(m.Groups[2]), Int(m.Groups[3])), false);
                case 1:
                    return Build(Int(m.Groups[1]), Months[m.Groups[2].Value[..3]], m.Groups[3].Success ? m.Groups[3].Value : null);
                case 2:
                    return Build(Int(m.Groups[2]), Months[m.Groups[1].Value[..3]], m.Groups[3].Success ? m.Groups[3].Value : null);
                default:
                    var a = Int(m.Groups[1]);
                    var b = Int(m.Groups[2]);
                    var year = Year(m.Groups[3].Value);
                    if (a > 12 && b <= 12)
                    {
                        return (new DateOnly(year, b, a), false); // day first (clear)
                    }

                    if (b > 12 && a <= 12)
                    {
                        return (new DateOnly(year, a, b), false); // month first (US style, clear)
                    }

                    return (new DateOnly(year, b, a), a != b); // assume day first; flag as ambiguous
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        (DateOnly, bool) Build(int day, int month, string? yearText)
        {
            if (yearText is { Length: > 0 })
            {
                return (new DateOnly(Year(yearText.Trim('\'')), month, day), false);
            }

            // No year printed ("Sat, 12 Dec"): the next such date from ~2 months before the import date.
            var candidate = new DateOnly(Reference.Year, month, Math.Min(day, DateTime.DaysInMonth(Reference.Year, month)));
            if (candidate < Reference.AddDays(-60))
            {
                candidate = candidate.AddYears(1);
            }

            return (candidate, false);
        }
    }

    private static int Year(string text)
    {
        var y = int.Parse(text, CultureInfo.InvariantCulture);
        return y < 100 ? 2000 + y : y;
    }

    private static TimeOnly? ToTime(Match m)
    {
        var hour = Int(m.Groups["h"]);
        var minute = m.Groups["m"].Success ? Int(m.Groups["m"]) : 0;
        var ampm = m.Groups["ap"].Success ? m.Groups["ap"].Value.Replace(".", "", StringComparison.Ordinal).ToLowerInvariant() : null;
        if (ampm == "pm" && hour < 12)
        {
            hour += 12;
        }
        else if (ampm == "am" && hour == 12)
        {
            hour = 0;
        }

        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeOnly(hour, minute) : null;
    }

    private static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);

    private static bool Overlaps(bool[] used, int start, int length)
    {
        for (var i = start; i < start + length && i < used.Length; i++)
        {
            if (used[i])
            {
                return true;
            }
        }

        return false;
    }

    private static void Mark(bool[] used, int start, int length)
    {
        for (var i = start; i < start + length && i < used.Length; i++)
        {
            used[i] = true;
        }
    }

    private const string MonthNames = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    [GeneratedRegex(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)")]
    private static partial Regex Iso();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})(?:st|nd|rd|th)?[\s\-/.]*(" + MonthNames + @")\.?(?![a-z])[\s\-/.,']*((?:19|20)\d{2}|'?\d{2}(?![\d:.]))?", RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthName();

    [GeneratedRegex(@"(?<![a-z])(" + MonthNames + @")\.?\s+(\d{1,2})(?:st|nd|rd|th)?(?!\d)(?:,?\s*((?:19|20)\d{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex MonthNameDay();

    [GeneratedRegex(@"(?<![\d.:/])(\d{1,2})[/.\-](\d{1,2})[/.\-]((?:19|20)\d{2}|\d{2})(?![\d:])")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"(?<![\d:.])(?<h>[01]?\d|2[0-3])[:.](?<m>[0-5]\d)(?![\d])(?:\s*(?<ap>[ap]\.?m\.?)(?![a-z]))?(?:\s*hrs?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Time24();

    [GeneratedRegex(@"(?<![\d:.])(?<h>1[0-2]|0?[1-9])\s*(?<ap>[ap]\.?m\.?)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex TimeAmPm();

    [GeneratedRegex(@"(?<![\d:.])(?<h>[01]\d|2[0-3])(?<m>[0-5]\d)\s*hrs?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimeHrs();

    [GeneratedRegex(@"^(?:(?:lead\s+)?(?:passenger|traveller|traveler|guest|attendee|ticket\s*holder|holder)s?(?:\s*names?)?(?:\s*details)?|name\s+of\s+(?:passenger|guest)|passenger\s+name|(?:full\s+)?name(?=\s*[:\-]))\b\s*(?:\(s\))?\s*[:\-–]?\s*(?<v>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelForNames();

    [GeneratedRegex(@"^\s*(?:\d+[.)]\s*)?(?:mr|mrs|ms|miss|mstr|master|dr|shri|smt)\.?\s+[a-z]", RegexOptions.IgnoreCase)]
    private static partial Regex TitledName();

    [GeneratedRegex(@"^\d+(?:\.\d+)+$")]
    private static partial Regex DottedNumber();

    [GeneratedRegex(@"^\s*(?:\d+[.)]\s*|(?:adult|child|infant)\s*\d*\s*[:\-]?\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingEnumeration();
}
