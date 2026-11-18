using System.Text.RegularExpressions;

namespace TravelVault.Core.Parsing;

/// <summary>A label and its value as read from the form ("PNR" → "1234567890"). <c>Row</c> is the row the label is on.</summary>
public sealed record FormField(string Label, string Value, int Row);

/// <summary>What the form reader found: label/value pairs and the rows as text (reading order).</summary>
public sealed record FormReading(IReadOnlyList<FormField> Fields, IReadOnlyList<string> Rows)
{
    public static readonly FormReading Empty = new([], []);

    public string Text => string.Join('\n', Rows);
}

/// <summary>
/// Reads a ticket the way a person does: it knows the labels tickets use (IRCTC/RailOne, airlines, travel sites,
/// bus and hotel bookings) and looks for each label's value <b>to its right</b> on the same row, or <b>below it</b>
/// in the same column when the label heads a column. This is what plain "text after the label" rules miss: the new
/// IRCTC/RailOne slip prints "PNR: | Train No./Name: | Class:" on one row and the values on the next.
/// </summary>
public static partial class FormReader
{
    /// <summary>
    /// Known labels, longest/most specific first (the regex alternation takes the first that matches).
    /// "Strong" labels are distinctive enough to start a new pair in the middle of a phrase without a colon
    /// ("… Boarding Time 13:55 Departure Time 14:40"); weak ones ("To", "Class") only count with a colon,
    /// at the start of a cell, or in a cell that is already a run of label/value pairs.
    /// </summary>
    private static readonly (string Pattern, bool Strong)[] Labels =
    [
        // Indian Railways (IRCTC e-ticket / ERS, RailOne)
        (@"train\s*no\.?\s*(?:&|and|/)\s*name", true), (@"train\s*(?:name|no\.?|number)", true),
        (@"pnr\s*(?:no\.?|number)?", true), (@"date\s*(?:&|and)\s*time\s*of\s*booking", true),
        (@"ticket\s*printing\s*time", true), (@"transaction\s*id", true), (@"booked\s*from", true), (@"boarding\s*at", true),
        (@"resv\.?\s*up\s*to", true), (@"reservation\s*up\s*to", true), (@"date\s*of\s*journey", true), (@"date\s*of\s*boarding", true),
        (@"scheduled\s*departure", true), (@"scheduled\s*arrival", true), (@"start\s*date", true), (@"quota", true),
        (@"distance", true), (@"booking\s*status", true), (@"current\s*status", true), (@"concession\s*code", true),
        (@"food\s*choice", true), (@"coach\s*(?:no\.?|number)?", false), (@"berth\s*(?:no\.?|number)?", false),

        // Airlines and travel sites
        (@"numeric\s*booking\s*id", true), (@"booking\s*(?:reference|ref\.?|id|code|no\.?|number)", true),
        (@"date\s*of\s*booking", true), (@"booking\s*date", true), (@"payment\s*status", true), (@"airline\s*pnr", true),
        (@"e-?ticket\s*(?:no\.?|number)", true), (@"ticket\s*(?:no\.?|number)", true), (@"flight\s*(?:no\.?|number)", true),
        (@"boarding\s*time", true), (@"departure\s*time", true), (@"arrival\s*time", true), (@"reporting\s*time", true),
        (@"passenger\s*name", true), (@"traveller\s*name", true), (@"guest\s*name", true), (@"seat\s*(?:no\.?|numbers?)", true),
        (@"departs", true), (@"arrives", true), (@"departure", true), (@"arrival", true), (@"gate", true), (@"terminal", true),
        (@"seats?", false), (@"zone", false), (@"sequence", false), (@"flight", false),

        // Bus
        (@"boarding\s*point", true), (@"dropping\s*point", true), (@"bus\s*type", true), (@"bus\s*operator", true), (@"journey\s*date", true),
        (@"travels", false), (@"operator", false), (@"landmark", false),

        // Hotels
        (@"check[\s-]?in", true), (@"check[\s-]?out", true), (@"confirmation\s*(?:number|no\.?|code)", true), (@"room\s*type", true),
        (@"hotel\s*name", true), (@"property\s*name", true), (@"pin\s*code", true), (@"rooms?", false), (@"nights?", false),
        (@"guests?", false), (@"pin", false),

        // General
        (@"train", false), (@"from", false), (@"to", false), (@"date", false), (@"time", false), (@"class", false), (@"status", false),
        (@"name", false), (@"age", false), (@"gender", false), (@"type", false),
    ];

    private static readonly Regex[] StrongLabels = Labels.Where(l => l.Strong).Select(l => new Regex("^(?:" + l.Pattern + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();

    private static readonly Regex AnyLabel = new(
        @"(?<![A-Za-z0-9])(?<label>" + string.Join('|', Labels.Select(l => l.Pattern)) + @")(?![A-Za-z])\s*\*?\s*(?<colon>:)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>One label on a row, with whatever value follows it in its cell.</summary>
    private sealed class Item
    {
        public required string Label { get; init; }

        public required double Left { get; init; }

        public required double Right { get; init; }

        public string Value { get; set; } = "";

        public double ValueLeft { get; set; }

        public double ValueRight { get; set; }
    }

    private sealed class RowItems(LayoutRow row)
    {
        public LayoutRow Row { get; } = row;

        public List<Item> Labels { get; } = [];

        /// <summary>Cells (or cell parts) that aren't labels: candidate values for labels around them.</summary>
        public List<LayoutCell> Free { get; } = [];

        public bool IsValueRow => Labels.Count == 0 && Free.Count > 0;
    }

    public static FormReading Read(TextLayout layout)
    {
        if (layout.IsEmpty)
        {
            return FormReading.Empty;
        }

        var rows = layout.Rows();
        var items = rows.Select(Analyze).ToList();
        var consumed = new HashSet<LayoutCell>();

        for (var r = 0; r < items.Count; r++)
        {
            var row = items[r];

            // 1) "PNR No:" [gap] "1234567890" — the value is the next free cell to the right on the same row.
            foreach (var item in row.Labels.Where(i => i.Value.Length == 0))
            {
                var nextLabel = row.Labels.Where(l => l.Left > item.Right).Select(l => l.Left).DefaultIfEmpty(double.MaxValue).Min();
                var right = row.Free.FirstOrDefault(c => !consumed.Contains(c) && c.Left >= item.Right && c.Right <= nextLabel);
                if (right is not null && row.Labels.Count(l => l.Value.Length == 0) == 1)
                {
                    Assign(item, [right]);
                    consumed.Add(right);
                }
            }

            var empty = row.Labels.Where(i => i.Value.Length == 0).ToList();
            if (empty.Count == 0 || r + 1 >= items.Count)
            {
                continue;
            }

            // 2) Labels stacked one per row, values stacked below them in the same order
            //    ("Booking Reference" / "Status" / "Date of Booking" / "ABC123" / "Confirmed" / "12 Oct").
            if (row.Labels.Count == 1 && row.Free.Count == 0 && StackedBlock(items, r, consumed) is > 0 and var stacked)
            {
                r += stacked - 1;
                continue;
            }

            // 3) A header row ("PNR: | Train No./Name: | Class:"): values sit under each label on the next row.
            var below = items[r + 1];
            if (!below.IsValueRow || below.Row.Page != row.Row.Page || below.Row.Top - row.Row.Bottom > 2.5 * row.Row.Height)
            {
                continue;
            }

            var all = row.Labels.OrderBy(l => l.Left).ToList();
            if (AssignColumns(all, empty, below, consumed) == 0)
            {
                continue;
            }

            // A value wrapped onto a second line ("from 14:00" under "Fri 12 Dec"): a shorter row of values
            // directly below. A full row is the next record of a table, not a continuation.
            if (r + 2 < items.Count && items[r + 2] is { IsValueRow: true } second && second.Row.Page == row.Row.Page
                && second.Row.Top - below.Row.Bottom <= 1.5 * below.Row.Height && second.Free.Count < below.Free.Count)
            {
                AssignColumns(all, empty, second, consumed, append: true);
            }
        }

        // 4) Values that wrapped: "Resv. Upto: YESVANTPUR" / "JN(YPR)" — a short free cell right under a value.
        for (var r = 0; r + 1 < items.Count; r++)
        {
            foreach (var item in items[r].Labels.Where(i => i.Value.Length > 0))
            {
                var unit = Math.Max(items[r].Row.Height * 0.4, 0.5);
                var next = items[r + 1];
                if (next.Row.Page != items[r].Row.Page || next.Row.Top - items[r].Row.Bottom > 1.5 * items[r].Row.Height || next.Labels.Count > 0)
                {
                    continue;
                }

                var wrap = next.Free.FirstOrDefault(c => !consumed.Contains(c) && c.Words.Count <= 4
                    && (Near(c.Left, item.ValueLeft, unit) || Near(c.Left, item.Left, unit)));
                if (wrap is not null && LooksLikeContinuation(item.Value, wrap.Text))
                {
                    item.Value = item.Value.EndsWith('-') ? item.Value + wrap.Text : item.Value + " " + wrap.Text;
                    consumed.Add(wrap);
                }
            }
        }

        var fields = new List<FormField>();
        for (var r = 0; r < items.Count; r++)
        {
            foreach (var item in items[r].Labels.Where(i => i.Value.Length > 0))
            {
                fields.Add(new FormField(item.Label, item.Value.Trim(' ', ':', '*', '-', '|'), r));
            }
        }

        return new FormReading(fields, rows.Select(r => r.Text).ToList());
    }

    /// <summary>Labels stacked one per row followed by as many single values; returns how many label rows were used.</summary>
    private static int StackedBlock(List<RowItems> items, int start, HashSet<LayoutCell> consumed)
    {
        var count = 0;
        while (start + count < items.Count && items[start + count] is { Labels.Count: 1, Free.Count: 0 } l && l.Labels[0].Value.Length == 0)
        {
            count++;
        }

        if (count < 2 || start + 2 * count > items.Count)
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
        {
            if (items[start + count + i] is not { IsValueRow: true, Free.Count: 1 })
            {
                return 0;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var cell = items[start + count + i].Free[0];
            Assign(items[start + i].Labels[0], [cell]);
            consumed.Add(cell);
        }

        return count * 2;
    }

    /// <summary>Puts each free cell of a row under the label whose column contains the cell's centre.</summary>
    private static int AssignColumns(List<Item> labels, List<Item> empty, RowItems values, HashSet<LayoutCell> consumed, bool append = false)
    {
        var assigned = 0;
        foreach (var cell in values.Free.Where(c => !consumed.Contains(c)))
        {
            var column = ColumnOf(labels, cell);
            if (column is null || !empty.Contains(column) || (append && column.Value.Length == 0))
            {
                continue;
            }

            if (column.Value.Length == 0)
            {
                Assign(column, [cell]);
            }
            else
            {
                column.Value += " " + cell.Text;
                column.ValueRight = Math.Max(column.ValueRight, cell.Right);
            }

            consumed.Add(cell);
            assigned++;
        }

        return assigned;
    }

    /// <summary>The column a cell belongs to: columns are split halfway between neighbouring labels.</summary>
    private static Item? ColumnOf(List<Item> labels, LayoutCell cell)
    {
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var width = Math.Max(label.Right - label.Left, 1);
            var left = i == 0 ? label.Left - Math.Max(width, 4 * (cell.Right - cell.Left) / Math.Max(cell.Text.Length, 1)) : (labels[i - 1].Right + label.Left) / 2;
            var right = i == labels.Count - 1 ? label.Right + Math.Max(3 * width, cell.Right - cell.Left) : (label.Right + labels[i + 1].Left) / 2;
            if (cell.CenterX >= left && cell.CenterX <= right)
            {
                return label;
            }
        }

        return null;
    }

    private static void Assign(Item item, IReadOnlyList<LayoutCell> cells)
    {
        item.Value = string.Join(' ', cells.Select(c => c.Text));
        item.ValueLeft = cells.Min(c => c.Left);
        item.ValueRight = cells.Max(c => c.Right);
    }

    /// <summary>Only clear cases: a date cut after the month ("14-Aug-") or a station name whose code wrapped ("JN(YPR)").</summary>
    private static bool LooksLikeContinuation(string value, string next) =>
        value.EndsWith('-') || (next.Contains('(') && !value.Contains('(') && !next.Contains(':'));

    private static bool Near(double a, double b, double unit) => a >= b - unit && a <= b + 2 * unit;

    /// <summary>Splits each cell of a row into label items and free text.</summary>
    private static RowItems Analyze(LayoutRow row)
    {
        var result = new RowItems(row);
        foreach (var cell in row.Cells)
        {
            AnalyzeCell(cell, result);
        }

        return result;
    }

    private static void AnalyzeCell(LayoutCell cell, RowItems result)
    {
        var text = cell.Text;
        var starts = new int[cell.Words.Count];
        for (int i = 0, pos = 0; i < cell.Words.Count; i++)
        {
            starts[i] = pos;
            pos += cell.Words[i].Text.Length + 1;
        }

        var accepted = new List<Match>();
        var sequence = false; // the cell starts with a colon-less label: a run like "Name X From A To B Flight No. 6E 206"
        foreach (Match m in AnyLabel.Matches(text))
        {
            var label = m.Groups["label"].Value;
            var startsWord = Array.IndexOf(starts, m.Index) >= 0 || (m.Index > 0 && !char.IsLetterOrDigit(text[m.Index - 1]));
            if (!startsWord)
            {
                continue;
            }

            var colon = m.Groups["colon"].Success;
            var atStart = m.Index == 0;
            var strong = StrongLabels.Any(r => r.IsMatch(label));
            if (colon || atStart || strong || sequence)
            {
                accepted.Add(m);
                sequence |= atStart && !colon;
            }
        }

        // A weak word at the start of a phrase, with no colon and nothing else labelled, is just a word: "from 14:00",
        // "To view your ticket…". (Alone in its cell — "To" over a value — it is still a column header.)
        if (accepted.Count == 1 && accepted[0].Index == 0 && !accepted[0].Groups["colon"].Success
            && accepted[0].Length < text.Length && !StrongLabels.Any(r => r.IsMatch(accepted[0].Groups["label"].Value)))
        {
            accepted.Clear();
        }

        // A generic "Something:" at the start of a cell is a label too (labels this reader doesn't know yet).
        if (GenericLabel().Match(text) is { Success: true } generic && (accepted.Count == 0 || accepted[0].Index >= generic.Index + generic.Length))
        {
            accepted.Insert(0, generic);
        }

        if (accepted.Count == 0)
        {
            result.Free.Add(cell);
            return;
        }

        if (accepted[0].Index > 0)
        {
            AddFree(cell, starts, 0, accepted[0].Index, result);
        }

        for (var i = 0; i < accepted.Count; i++)
        {
            var m = accepted[i];
            var valueStart = m.Index + m.Length;
            var valueEnd = i + 1 < accepted.Count ? accepted[i + 1].Index : text.Length;
            var value = valueStart < valueEnd ? text[valueStart..valueEnd].Trim() : "";
            var hasColon = m.Groups["colon"].Success;

            // Without a colon, a long run of words after a weak label is a sentence ("To view your ticket…"), not a value.
            if (!hasColon && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6)
            {
                AddFree(cell, starts, m.Index, valueEnd, result);
                continue;
            }

            var (labelLeft, labelRight) = Span(cell, starts, m.Index, m.Index + m.Groups["label"].Length);
            var item = new Item { Label = Clean(m.Groups["label"].Value), Left = labelLeft, Right = labelRight, Value = value };
            if (value.Length > 0)
            {
                (item.ValueLeft, item.ValueRight) = Span(cell, starts, valueStart, valueEnd);
            }

            result.Labels.Add(item);
        }
    }

    private static void AddFree(LayoutCell cell, int[] starts, int from, int to, RowItems result)
    {
        var words = cell.Words.Where((w, i) => starts[i] >= from && starts[i] < to).ToList();
        if (words.Count > 0)
        {
            result.Free.Add(new LayoutCell(words));
        }
    }

    /// <summary>Left and right of the words covering characters [from, to) of the cell text.</summary>
    private static (double Left, double Right) Span(LayoutCell cell, int[] starts, int from, int to)
    {
        double left = double.MaxValue, right = double.MinValue;
        for (var i = 0; i < cell.Words.Count; i++)
        {
            var wordStart = starts[i];
            var wordEnd = wordStart + cell.Words[i].Text.Length;
            if (wordEnd > from && wordStart < to)
            {
                left = Math.Min(left, cell.Words[i].Left);
                right = Math.Max(right, cell.Words[i].Right);
            }
        }

        return left == double.MaxValue ? (cell.Left, cell.Right) : (left, right);
    }

    private static string Clean(string label) => Regex.Replace(label.Trim(' ', ':', '*', '.'), @"\s+", " ");

    [GeneratedRegex(@"^(?<label>[A-Za-z][A-Za-z.&/' ]{1,28}?)\s*\*?\s*(?<colon>:)(?!\d)")]
    private static partial Regex GenericLabel();
}
