using System.Globalization;
using System.Text;

namespace TravelVault.Core.Travelers;

/// <summary>
/// A person's name in a comparable form. Tickets print names in many styles:
/// "MR RAHUL SHARMA", "SHARMA/RAHUL MR", "Sharma, Rahul", "RAHUL K SHARMA", "Ms. Priya Sharma".
/// Normalising removes case, accents, punctuation and titles, and turns "SURNAME/GIVEN" into "GIVEN SURNAME".
/// </summary>
public sealed class PersonName : IEquatable<PersonName>
{
    private static readonly HashSet<string> Titles =
    [
        "MR", "MRS", "MS", "MISS", "MSTR", "MASTER", "DR", "PROF", "SHRI", "SMT", "KUMARI", "KUM", "MX", "SIR", "MADAM", "CHD", "INF",
    ];

    private PersonName(IReadOnlyList<string> tokens) => Tokens = tokens;

    /// <summary>Upper-case tokens in given-name-first order, titles removed.</summary>
    public IReadOnlyList<string> Tokens { get; }

    public string Normalized => string.Join(' ', Tokens);

    public bool IsEmpty => Tokens.Count == 0;

    public static PersonName Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PersonName([]);
        }

        var text = RemoveAccents(raw).ToUpperInvariant().Trim();

        // "SURNAME/GIVEN MR" (airline / IATA style) → "GIVEN MR SURNAME"
        var slash = text.IndexOf('/');
        if (slash > 0 && slash < text.Length - 1)
        {
            text = StripGluedTitle(text[(slash + 1)..].Trim()) + " " + text[..slash];
        }
        else
        {
            // "Sharma, Rahul" → "Rahul Sharma"
            var comma = text.IndexOf(',');
            if (comma > 0 && comma < text.Length - 1 && text.Count(c => c == ',') == 1)
            {
                text = text[(comma + 1)..] + " " + text[..comma];
            }
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                current.Append(c);
            }
            else if (c is '\'' or '’')
            {
                // O'Brien → OBRIEN
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return new PersonName(tokens);

        void Flush()
        {
            if (current.Length > 0)
            {
                var token = current.ToString();
                if (!Titles.Contains(token))
                {
                    tokens.Add(token);
                }

                current.Clear();
            }
        }
    }

    public bool Equals(PersonName? other) => other is not null && Normalized == other.Normalized;

    public override bool Equals(object? obj) => Equals(obj as PersonName);

    public override int GetHashCode() => Normalized.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Normalized;

    /// <summary>Display form: "Rahul Sharma".</summary>
    public string ToTitleCase() =>
        string.Join(' ', Tokens.Select(t => t.Length == 1 ? t : t[0] + t[1..].ToLowerInvariant()));

    /// <summary>
    /// Boarding-pass barcodes squeeze the title onto the given name: "SHARMA/RAHULMR", "SHARMA/PRIYAMRS".
    /// Only used for the airline "SURNAME/GIVEN" style, and only when a real name (≥ 3 letters) remains.
    /// </summary>
    private static string StripGluedTitle(string given)
    {
        var lastSpace = given.LastIndexOf(' ');
        var head = lastSpace >= 0 ? given[..(lastSpace + 1)] : "";
        var last = lastSpace >= 0 ? given[(lastSpace + 1)..] : given;
        foreach (var title in new[] { "MSTR", "MISS", "MRS", "MR", "MS", "DR" })
        {
            if (last.Length >= title.Length + 3 && last.EndsWith(title, StringComparison.Ordinal))
            {
                return head + last[..^title.Length];
            }
        }

        return given;
    }

    private static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
