using System.Security.Cryptography;
using System.Text;

namespace TravelVault.Core.Security;

/// <summary>
/// The printable code that unlocks a backup file: 30 random Crockford-Base32 characters (150 bits) + 2 check
/// characters, shown as 8 groups of 4 ("7K2M-…"). Crockford Base32 avoids I, L, O and U, and reading accepts
/// common mix-ups (O→0, I/L→1), so a handwritten code still works. The check characters catch typos before
/// a slow key derivation is attempted.
/// </summary>
public sealed class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int DataLength = 30;
    private const int CheckLength = 2;

    private RecoveryCode(string normalized) => Normalized = normalized;

    /// <summary>32 characters, no separators.</summary>
    public string Normalized { get; }

    public static RecoveryCode Generate()
    {
        var chars = new char[DataLength];
        for (var i = 0; i < DataLength; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var data = new string(chars);
        return new RecoveryCode(data + Check(data));
    }

    /// <summary>Accepts the code with or without dashes/spaces, in any case, with O/I/L look-alikes.</summary>
    public static bool TryParse(string? text, out RecoveryCode? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var builder = new StringBuilder(DataLength + CheckLength);
        foreach (var raw in text.ToUpperInvariant())
        {
            var c = raw switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => raw,
            };
            if (c is '-' or ' ')
            {
                continue;
            }

            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }

            builder.Append(c);
        }

        if (builder.Length != DataLength + CheckLength)
        {
            return false;
        }

        var all = builder.ToString();
        if (Check(all[..DataLength]) != all[DataLength..])
        {
            return false;
        }

        code = new RecoveryCode(all);
        return true;
    }

    /// <summary>"XXXX-XXXX-…" for display.</summary>
    public string Display() => string.Join("-", Enumerable.Range(0, Normalized.Length / 4).Select(i => Normalized.Substring(i * 4, 4)));

    private static string Check(string data)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(data));
        return new string([Alphabet[hash[0] & 31], Alphabet[hash[1] & 31]]);
    }
}

/// <summary>Turns a recovery code into the backup encryption key (PBKDF2-SHA256, 600,000 rounds, random salt).</summary>
public static class BackupKey
{
    public const int SaltSize = 16;
    public const int DefaultIterations = 600_000;

    public static byte[] Derive(RecoveryCode code, ReadOnlySpan<byte> salt, int iterations = DefaultIterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(code.Normalized), salt, iterations, HashAlgorithmName.SHA256, AeadBox.KeySize);
}

/// <summary>
/// The 6-digit code two people compare (by phone call or in person) before a remote join is approved.
/// It is derived from both devices' public keys and the invite secret; a person in the middle who swapped a key
/// would produce a different code on one of the phones.
/// </summary>
public static class SafetyCode
{
    public static string Compute(DevicePublicKeys admin, DevicePublicKeys joiner, ReadOnlySpan<byte> inviteSecret)
    {
        var hash = SHA256.HashData([.. admin.Fingerprint(), .. joiner.Fingerprint(), .. inviteSecret]);
        var number = ((uint)hash[0] << 24 | (uint)hash[1] << 16 | (uint)hash[2] << 8 | hash[3]) % 1_000_000;
        return number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
