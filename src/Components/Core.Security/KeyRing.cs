using System.Security.Cryptography;

namespace TravelVault.Core.Security;

/// <summary>
/// Protects the Device Master Key (DMK). On Android this is the Keystore (hardware-backed, non-exportable key);
/// tests use <see cref="InMemoryKeyVault"/>.
/// </summary>
public interface IKeyVault
{
    /// <summary>Encrypts the DMK so it can be stored in a file.</summary>
    byte[] Wrap(byte[] masterKey);

    /// <returns>The DMK, or null when the stored blob can't be opened (e.g. the Keystore key was erased).</returns>
    byte[]? Unwrap(byte[] wrapped);

    /// <summary>Destroys the wrapping key: everything encrypted under the DMK becomes unreadable ("Erase everything").</summary>
    void Destroy();
}

/// <summary>A vault that keeps its wrapping key in memory. Only for tests and desktop tools.</summary>
public sealed class InMemoryKeyVault : IKeyVault
{
    private byte[]? _wrappingKey = AeadBox.NewKey();

    public byte[] Wrap(byte[] masterKey) => AeadBox.Seal(Key(), masterKey, "dmk");

    public byte[]? Unwrap(byte[] wrapped) => _wrappingKey is null ? null : AeadBox.Open(_wrappingKey, wrapped, "dmk");

    public void Destroy() => _wrappingKey = null;

    private byte[] Key() => _wrappingKey ?? throw new InvalidOperationException("The vault was destroyed.");
}

/// <summary>
/// Holds the unlocked Device Master Key while the app runs and derives / wraps every other key with it:
/// database key, per-file keys, family keys and the device's private keys.
/// </summary>
public sealed class KeyRing : IDisposable
{
    private readonly byte[] _masterKey;

    private KeyRing(byte[] masterKey) => _masterKey = masterKey;

    /// <summary>Creates a new DMK and returns it with its wrapped form (to store on disk).</summary>
    public static (KeyRing Ring, byte[] Wrapped) CreateNew(IKeyVault vault)
    {
        var key = AeadBox.NewKey();
        return (new KeyRing(key), vault.Wrap(key));
    }

    public static KeyRing? Unlock(IKeyVault vault, byte[] wrapped) =>
        vault.Unwrap(wrapped) is { Length: AeadBox.KeySize } key ? new KeyRing(key) : null;

    /// <summary>Restoring from a backup: the DMK comes from the backup, then gets wrapped by this phone's vault.</summary>
    public static KeyRing FromRaw(byte[] masterKey) =>
        masterKey.Length == AeadBox.KeySize ? new KeyRing([.. masterKey]) : throw new ArgumentException("Bad key length.", nameof(masterKey));

    /// <summary>Key for the encrypted SQLite database (derived, never stored).</summary>
    public byte[] DatabaseKey() => KeyDerivation.Derive(_masterKey, "db");

    /// <summary>Encrypts a secret (file key, family key, private key) for storage, bound to what it is.</summary>
    public byte[] Protect(ReadOnlySpan<byte> secret, string label) => AeadBox.Seal(_masterKey, secret, "ring/" + label);

    public byte[]? Unprotect(ReadOnlySpan<byte> protectedSecret, string label) => AeadBox.Open(_masterKey, protectedSecret, "ring/" + label);

    /// <summary>Only for writing the encrypted backup (which is itself encrypted with the recovery-code key).</summary>
    public byte[] ExportRawForBackup() => [.. _masterKey];

    public void Dispose() => CryptographicOperations.ZeroMemory(_masterKey);
}
