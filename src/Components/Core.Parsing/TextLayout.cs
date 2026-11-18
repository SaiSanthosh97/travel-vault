namespace TravelVault.Core.Parsing;

/// <summary>
/// A word with its box on the page. Units only need to be consistent within one page: PDF points, image pixels
/// (OCR) or character cells (plain text). Y grows downwards (top of the page = 0).
/// </summary>
public sealed record LayoutWord(string Text, double Left, double Top, double Right, double Bottom, int Page = 0)
{
    public double CenterX => (Left + Right) / 2;

    public double CenterY => (Top + Bottom) / 2;

    public double Height => Math.Max(Bottom - Top, 0.01);

    /// <summary>Average width of one character: the yardstick for "is this gap a space or a new column?".</summary>
    public double CharWidth => Math.Max((Right - Left) / Math.Max(Text.Length, 1), 0.01);
}

/// <summary>Words that sit close together on one row: a phrase such as "AJMER JN (AII)" or "PNR No: 1234567890".</summary>
public sealed class LayoutCell(IReadOnlyList<LayoutWord> words)
{
    public IReadOnlyList<LayoutWord> Words { get; } = words;

    public string Text { get; } = string.Join(' ', words.Select(w => w.Text));

    public double Left => Words[0].Left;

    public double Right => Words[^1].Right;

    public double CenterX => (Left + Right) / 2;
}

/// <summary>One visual row of a page, left to right, split into cells at wide gaps.</summary>
public sealed class LayoutRow(int page, double top, double bottom, IReadOnlyList<LayoutCell> cells)
{
    public int Page { get; } = page;

    public double Top { get; } = top;

    public double Bottom { get; } = bottom;

    public double Height => Math.Max(Bottom - Top, 0.01);

    public IReadOnlyList<LayoutCell> Cells { get; } = cells;

    /// <summary>The row as text; cells are separated by three spaces so text rules still see the columns.</summary>
    public string Text => string.Join("   ", Cells.Select(c => c.Text));
}

/// <summary>
/// Where every word is on the page. Tickets are forms: a value is either next to its label ("PNR: 123") or under
/// it (a header row "PNR | Train | Class" with the values on the row below). Plain text loses the "under" relation,
/// so the reader works on positions instead. Built from PDF word boxes, OCR word boxes, or, for plain text, from
/// character columns (monospaced text keeps its columns).
/// </summary>
public sealed class TextLayout
{
    private TextLayout(IReadOnlyList<LayoutWord> words) => Words = words;

    public IReadOnlyList<LayoutWord> Words { get; }

    public bool IsEmpty => Words.Count == 0;

    /// <summary>The page as text, row by row, left to right.</summary>
    public string Text => string.Join('\n', Rows().Select(r => r.Text));

    public static TextLayout FromWords(IEnumerable<LayoutWord> words) =>
        new(words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Select(w => w with { Text = w.Text.Trim() }).ToList());

    /// <summary>Plain text as a grid: each character is one unit wide and each line one unit high.</summary>
    public static TextLayout FromGrid(string text)
    {
        var words = new List<LayoutWord>();
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var row = 0; row < lines.Length; row++)
        {
            var line = lines[row].Replace('\t', ' ');
            var i = 0;
            while (i < line.Length)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    i++;
                    continue;
                }

                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                words.Add(new LayoutWord(line[start..i], start, row, i, row + 1));
            }
        }

        return new TextLayout(words);
    }

    /// <summary>Rows in reading order (page, then top to bottom), each split into cells at gaps wider than ~1.5 characters.</summary>
    public IReadOnlyList<LayoutRow> Rows()
    {
        var rows = new List<LayoutRow>();
        foreach (var page in Words.GroupBy(w => w.Page).OrderBy(g => g.Key))
        {
            // Group words whose vertical centres are within half a line of the row's centre.
            var pending = new List<(List<LayoutWord> Words, double Center, double Height)>();
            foreach (var word in page.OrderBy(w => w.CenterY).ThenBy(w => w.Left))
            {
                var last = pending.Count > 0 ? pending[^1] : default;
                if (last.Words is not null && Math.Abs(word.CenterY - last.Center) <= 0.5 * Math.Max(word.Height, last.Height))
                {
                    last.Words.Add(word);
                    var center = last.Words.Average(w => w.CenterY);
                    pending[^1] = (last.Words, center, last.Words.Average(w => w.Height));
                }
                else
                {
                    pending.Add(([word], word.CenterY, word.Height));
                }
            }

            foreach (var (words, _, _) in pending)
            {
                var ordered = words.OrderBy(w => w.Left).ToList();
                var cells = new List<LayoutCell>();
                var current = new List<LayoutWord> { ordered[0] };
                for (var i = 1; i < ordered.Count; i++)
                {
                    var gap = ordered[i].Left - ordered[i - 1].Right;
                    var unit = Math.Max(ordered[i].CharWidth, ordered[i - 1].CharWidth);
                    if (gap > 1.5 * unit)
                    {
                        cells.Add(new LayoutCell(current));
                        current = [];
                    }

                    current.Add(ordered[i]);
                }

                cells.Add(new LayoutCell(current));
                rows.Add(new LayoutRow(page.Key, words.Min(w => w.Top), words.Max(w => w.Bottom), cells));
            }
        }

        return rows;
    }
}
