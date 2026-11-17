using System.Security.Cryptography;

namespace TravelVault.Core.Security;

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
