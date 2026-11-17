using System.Security.Cryptography;
using System.Text;

namespace TravelVault.Core.Security;

/// <summary>
/// Authenticated encryption with a 32-byte key (AES-256-GCM). Adapted from Use It Before's SealedBox.
/// Output layout: version(1) | nonce(12) | ciphertext | tag(16).
/// A <c>label</c> (associated data) is bound to every box, so a box made for one purpose
/// (e.g. "file/abc") can't be swapped in for another (e.g. "file/xyz"): opening it with a different label fails.
/// </summary>
public static class AeadBox
{
    public const int KeySize = 32;
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int Overhead = 1 + NonceSize + TagSize;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string label)
    {
        CheckKey(key);
        var output = new byte[Overhead + plaintext.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(1 + NonceSize, plaintext.Length),
            output.AsSpan(1 + NonceSize + plaintext.Length, TagSize),
            Encoding.UTF8.GetBytes(label));
        return output;
    }

    /// <returns>The plaintext, or null when the box was not made with this key and label, or was changed.</returns>
    public static byte[]? Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> box, string label)
    {
        CheckKey(key);
        if (box.Length < Overhead || box[0] != Version)
        {
            return null;
        }

        var plaintext = new byte[box.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                box.Slice(1, NonceSize),
                box.Slice(1 + NonceSize, plaintext.Length),
                box.Slice(1 + NonceSize + plaintext.Length, TagSize),
                plaintext,
                Encoding.UTF8.GetBytes(label));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    private static void CheckKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The key must be {KeySize} bytes.", nameof(key));
        }
    }
}

/// <summary>Derives purpose-specific keys from one key (HKDF-SHA256), so one secret never serves two purposes.</summary>
public static class KeyDerivation
{
    public static byte[] Derive(ReadOnlySpan<byte> key, string purpose, int length = AeadBox.KeySize) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.ToArray(), length, salt: [], info: Encoding.UTF8.GetBytes("travelvault/" + purpose));
}
