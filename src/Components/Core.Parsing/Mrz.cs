using System.Globalization;

namespace TravelVault.Core.Parsing;

public enum MrzKind
{
    Passport,
    Visa,
}

public sealed record MrzData(
    MrzKind Kind,
    string IssuingCountry,
    string Surname,
    string GivenNames,
    string DocumentNumber,
    string Nationality,
    DateOnly? BirthDate,
    DateOnly? ExpiryDate,
    bool ChecksPassed)
{
    public string FullName => (GivenNames + " " + Surname).Trim();
}

/// <summary>
/// Reads the Machine Readable Zone (ICAO 9303): the two lines of "&lt;&lt;&lt;" text at the bottom of every passport
/// photo page and on visas. Each important field has a check digit (weights 7-3-1), so a correct read can be
/// verified, and OCR mistakes like O↔0 in number fields can be repaired.
/// Supports passports (TD3: 2×44) and visas (MRV-A 2×44, MRV-B 2×36).
/// </summary>
public static class MrzParser
{
    public static MrzData? Find(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Split('\n')
            .Select(Clean)
            .Where(l => l.Length >= 30 && l.Count(c => c == '<') >= 3)
            .ToList();

        for (var i = 0; i < lines.Count - 1; i++)
        {
            var first = lines[i];
            var second = lines[i + 1];
            if (first[0] is 'P' or 'V' && second.Length >= 28)
            {
                var data = Parse(first, second, today);
                if (data is not null)
                {
                    return data;
                }
            }
        }

        return null;
    }

    internal static MrzData? Parse(string line1, string line2, DateOnly today)
    {
        var kind = line1[0] == 'P' ? MrzKind.Passport : MrzKind.Visa;
        if (line1.Length < 10)
        {
            return null;
        }

        var issuer = Letters(line1.Substring(2, 3)).Replace("<", "", StringComparison.Ordinal);
        var names = line1[5..].Split("<<", 2, StringSplitOptions.None);
        var surname = names[0].Replace('<', ' ').Trim();
        var given = names.Length > 1 ? names[1].Replace('<', ' ').Trim() : "";
        given = string.Join(' ', given.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var number = line2[..9];
        var numberOk = Check(number, line2[9]);
        if (!numberOk)
        {
            // OCR often reads 0 as O in the number; try the digit form.
            var repaired = new string(number.Select(c => c == 'O' ? '0' : c).ToArray());
            if (Check(repaired, line2[9]))
            {
                number = repaired;
                numberOk = true;
            }
        }

        var nationality = Letters(line2.Substring(10, 3)).Replace("<", "", StringComparison.Ordinal);
        var birthText = Digits(line2.Substring(13, 6));
        var expiryText = Digits(line2.Substring(21, 6));
        var birthOk = Check(birthText, DigitChar(line2[19]));
        var expiryOk = Check(expiryText, DigitChar(line2[27]));

        var data = new MrzData(
            kind,
            issuer,
            surname,
            given,
            number.Replace("<", "", StringComparison.Ordinal),
            nationality,
            ParseDate(birthText, today, isExpiry: false),
            ParseDate(expiryText, today, isExpiry: true),
            numberOk && birthOk && expiryOk);

        // Require at least the number or the expiry to verify; otherwise this is probably not an MRZ.
        return (numberOk || expiryOk) && surname.Length > 0 ? data : null;
    }

    /// <summary>ICAO 9303 check digit: weights 7,3,1 repeating; 0–9 as is, A–Z = 10–35, '&lt;' = 0.</summary>
    internal static int CheckDigit(string field)
    {
        int[] weights = [7, 3, 1];
        var sum = 0;
        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];
            var value = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 0,
            };
            sum += value * weights[i % 3];
        }

        return sum % 10;
    }

    private static bool Check(string field, char digit) => digit is >= '0' and <= '9' && CheckDigit(field) == digit - '0';

    private static DateOnly? ParseDate(string yymmdd, DateOnly today, bool isExpiry)
    {
        if (!DateOnly.TryParseExact(yymmdd, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        // Two-digit years: expiry dates are in the future (20YY); birth dates are not after today (19YY if needed).
        var year = 2000 + parsed.Year % 100;
        if (!isExpiry && year > today.Year)
        {
            year -= 100;
        }

        return new DateOnly(year, parsed.Month, parsed.Day);
    }

    private static string Clean(string line) =>
        new string(line.Trim().ToUpperInvariant().Replace('«', '<').Where(c => c != ' ').ToArray());

    private static string Digits(string s) => new(s.Select(DigitChar).ToArray());

    private static char DigitChar(char c) => c switch
    {
        'O' or 'Q' or 'D' => '0',
        'I' or 'L' => '1',
        'Z' => '2',
        'S' => '5',
        'B' => '8',
        _ => c,
    };

    private static string Letters(string s) => new(s.Select(c => c switch { '0' => 'O', '1' => 'I', '5' => 'S', '8' => 'B', _ => c }).ToArray());
}
