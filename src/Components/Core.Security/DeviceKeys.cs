using System.Security.Cryptography;
using System.Text;

namespace TravelVault.Core.Security;

/// <summary>
/// The public half of a device's identity: a signing key (ECDSA P-256, proves who wrote a message)
/// and an agreement key (ECDH P-256, lets others encrypt something only this device can open).
/// Stored and sent as SubjectPublicKeyInfo (DER) bytes.
/// </summary>
public sealed record DevicePublicKeys(byte[] Signing, byte[] Agreement)
{
    /// <summary>Short, stable fingerprint for display and for safety codes.</summary>
    public byte[] Fingerprint() => SHA256.HashData([.. Signing, .. Agreement]);
}

/// <summary>
/// A device's private keys. Created once per installation; the private parts never leave the phone
/// except inside the user's encrypted backup.
/// </summary>
public sealed class DeviceKeyPair : IDisposable
{
    private readonly ECDsa _signing;
    private readonly ECDiffieHellman _agreement;

    private DeviceKeyPair(ECDsa signing, ECDiffieHellman agreement)
    {
        _signing = signing;
        _agreement = agreement;
        Public = new DevicePublicKeys(signing.ExportSubjectPublicKeyInfo(), agreement.ExportSubjectPublicKeyInfo());
    }

    public DevicePublicKeys Public { get; }

    public static DeviceKeyPair Create() =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Restores keys exported by <see cref="ExportPrivate"/> (PKCS#8).</summary>
    public static DeviceKeyPair Import(byte[] signingPkcs8, byte[] agreementPkcs8)
    {
        var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(signingPkcs8, out _);
        var agreement = ECDiffieHellman.Create();
        agreement.ImportPkcs8PrivateKey(agreementPkcs8, out _);
        return new DeviceKeyPair(signing, agreement);
    }

    public (byte[] Signing, byte[] Agreement) ExportPrivate() =>
        (_signing.ExportPkcs8PrivateKey(), _agreement.ExportPkcs8PrivateKey());

    /// <summary>64-byte ECDSA P-256 / SHA-256 signature (IEEE P1363 r||s form: fixed size, compact).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _signing.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>Opens a <see cref="RecipientBox"/> addressed to this device.</summary>
    public byte[]? OpenSealed(ReadOnlySpan<byte> box, string label) => RecipientBox.Open(_agreement, box, label);

    public void Dispose()
    {
        _signing.Dispose();
        _agreement.Dispose();
    }
}

public static class Signatures
{
    public const int Size = 64;

    public static bool Verify(byte[] signingPublicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != Size)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(signingPublicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false; // Malformed key: treat as "not verified", never as an error the sender can trigger.
        }
    }
}

/// <summary>
/// Encrypts data so only one device can open it (ECIES): a fresh ephemeral P-256 key agrees a secret with the
/// recipient's agreement key; HKDF turns it into an AES-256-GCM key. Used for handing family keys to a joining
/// device and for key rotation, so the new key never travels under the old key.
/// Layout: ephemeralPublicKeyLength(2) | ephemeralPublicKey (SPKI) | AeadBox.
/// </summary>
public static class RecipientBox
{
    public static byte[] Seal(byte[] recipientAgreementPublicKey, ReadOnlySpan<byte> plaintext, string label)
    {
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(recipientAgreementPublicKey, out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var key = SharedKey(ephemeral, recipient.PublicKey, ephemeralPublic, recipientAgreementPublicKey);
        try
        {
            var box = AeadBox.Seal(key, plaintext, "recipient/" + label);
            var output = new byte[2 + ephemeralPublic.Length + box.Length];
            output[0] = (byte)(ephemeralPublic.Length >> 8);
            output[1] = (byte)ephemeralPublic.Length;
            ephemeralPublic.CopyTo(output, 2);
            box.CopyTo(output, 2 + ephemeralPublic.Length);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[]? Open(ECDiffieHellman recipient, ReadOnlySpan<byte> sealedData, string label)
    {
        if (sealedData.Length < 2)
        {
            return null;
        }

        var length = (sealedData[0] << 8) | sealedData[1];
        if (sealedData.Length < 2 + length + AeadBox.Overhead)
        {
            return null;
        }

        var ephemeralPublic = sealedData.Slice(2, length).ToArray();
        try
        {
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
            var key = SharedKey(recipient, ephemeral.PublicKey, ephemeralPublic, recipient.ExportSubjectPublicKeyInfo());
            try
            {
                return AeadBox.Open(key, sealedData[(2 + length)..], "recipient/" + label);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] SharedKey(ECDiffieHellman own, ECDiffieHellmanPublicKey other, byte[] ephemeralPublic, byte[] recipientPublic)
    {
        var secret = own.DeriveRawSecretAgreement(other);
        try
        {
            // Bind both public keys into the derived key (prevents key-substitution tricks).
            var info = Encoding.UTF8.GetBytes("travelvault/recipient-box");
            var salt = SHA256.HashData([.. ephemeralPublic, .. recipientPublic]);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, AeadBox.KeySize, salt, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
