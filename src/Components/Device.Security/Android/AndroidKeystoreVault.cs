using Android.Security.Keystore;
using Android.Util;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Android.Runtime;
using TravelVault.Core.Security;

namespace TravelVault.Device.Security;

/// <summary>
/// Wraps the device master key with an AES-256-GCM key that lives in the Android Keystore: generated inside secure
/// hardware (StrongBox when the phone has it, otherwise the TEE), never exportable. Copying the app's files to
/// another phone is useless without this key, and deleting it ("Erase everything") makes all data unreadable at once.
/// The key does not require user authentication, so background sync and reminders keep working while the phone is
/// locked; the app lock is enforced by the UI (see Security Model 7.2).
/// </summary>
public sealed class AndroidKeystoreVault(string alias = "travelvault_master_wrap") : IKeyVault
{
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const byte FormatVersion = 1;
    private const int IvSize = 12;

    public byte[] Wrap(byte[] masterKey)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        var iv = cipher.GetIV()!;
        var sealedKey = cipher.DoFinal(masterKey)!;
        return [FormatVersion, .. iv, .. sealedKey];
    }

    public byte[]? Unwrap(byte[] wrapped)
    {
        if (wrapped.Length <= 1 + IvSize || wrapped[0] != FormatVersion || LoadKey() is not { } key)
        {
            return null;
        }

        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, wrapped[1..(1 + IvSize)]));
            return cipher.DoFinal(wrapped[(1 + IvSize)..]);
        }
        catch (Exception ex) when (ex is GeneralSecurityException or Java.Lang.IllegalStateException)
        {
            Log.Warn("TravelVault", $"Master key could not be unwrapped: {ex.GetType().Name}");
            return null;
        }
    }

    public void Destroy()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.ContainsAlias(alias))
        {
            store.DeleteEntry(alias);
        }
    }

    /// <summary>True when the key sits in a dedicated security chip (StrongBox), for the About screen.</summary>
    public bool IsHardwareBacked()
    {
        if (LoadKey() is not { } key)
        {
            return false;
        }

        // Java objects must be converted with JavaCast, not a C# cast: a C# cast throws InvalidCastException
        // (this crashed the Settings screen in 0.1.0). Only informational, so any failure just means "don't know".
        try
        {
            var factory = SecretKeyFactory.GetInstance(key.Algorithm, Provider)!;
            var spec = factory.GetKeySpec(key.JavaCast<ISecretKey>(), Java.Lang.Class.FromType(typeof(KeyInfo)));
            if (spec?.JavaCast<KeyInfo>() is not { } info)
            {
                return false;
            }

            return OperatingSystem.IsAndroidVersionAtLeast(31) ? info.SecurityLevel != (int)KeyStoreSecurityLevel.Software : info.IsInsideSecureHardware;
        }
        catch (Exception ex) when (ex is GeneralSecurityException or InvalidCastException or Java.Lang.Exception)
        {
            return false;
        }
    }

    private IKey? LoadKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        return store.GetKey(alias, null);
    }

    private IKey GetOrCreateKey() => LoadKey() ?? (OperatingSystem.IsAndroidVersionAtLeast(28) ? TryCreate(strongBox: true) : null) ?? TryCreate(strongBox: false)!;

    private IKey? TryCreate(bool strongBox)
    {
        try
        {
            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
            var spec = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                .SetRandomizedEncryptionRequired(true)!;
            if (strongBox && OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                spec.SetIsStrongBoxBacked(true);
            }

            generator.Init(spec.Build());
            return generator.GenerateKey();
        }
        catch (Exception ex) when (strongBox && ex is ProviderException or StrongBoxUnavailableException)
        {
            return null; // no StrongBox on this phone: fall back to the TEE
        }
    }
}
