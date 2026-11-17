using TravelVault.Core.Security;

namespace TravelVault.Core.Vault;

/// <summary>This phone's identity: member id, name, private keys (wrapped by the key ring) and the recovery code.</summary>
public sealed record DeviceIdentity(string MemberId, string DisplayName, byte[] SigningPrivateKey, byte[] AgreementPrivateKey)
{
    public DeviceKeyPair ToKeyPair() => DeviceKeyPair.Import(SigningPrivateKey, AgreementPrivateKey);
}

/// <summary>Stores the device identity and recovery code in the encrypted settings, each additionally wrapped by the key ring.</summary>
public sealed class DeviceIdentityStore(ISettingsStore settings, KeyRing ring)
{
    private const string MemberIdKey = "device.memberId";
    private const string NameKey = "device.name";
    private const string SigningKey = "device.signing";
    private const string AgreementKey = "device.agreement";
    private const string RecoveryKey = "device.recovery";

    public async Task SaveAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        await settings.SetAsync(MemberIdKey, identity.MemberId, cancellationToken);
        await settings.SetAsync(NameKey, identity.DisplayName, cancellationToken);
        await settings.SetAsync(SigningKey, Convert.ToBase64String(ring.Protect(identity.SigningPrivateKey, "device/signing")), cancellationToken);
        await settings.SetAsync(AgreementKey, Convert.ToBase64String(ring.Protect(identity.AgreementPrivateKey, "device/agreement")), cancellationToken);
    }

    public async Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var id = await settings.GetAsync(MemberIdKey, cancellationToken);
        var name = await settings.GetAsync(NameKey, cancellationToken);
        var signing = await settings.GetAsync(SigningKey, cancellationToken);
        var agreement = await settings.GetAsync(AgreementKey, cancellationToken);
        if (id is null || name is null || signing is null || agreement is null)
        {
            return null;
        }

        var s = ring.Unprotect(Convert.FromBase64String(signing), "device/signing");
        var a = ring.Unprotect(Convert.FromBase64String(agreement), "device/agreement");
        return s is null || a is null ? null : new DeviceIdentity(id, name, s, a);
    }

    public Task RenameAsync(string displayName, CancellationToken cancellationToken = default) => settings.SetAsync(NameKey, displayName.Trim(), cancellationToken);

    public Task SaveRecoveryCodeAsync(RecoveryCode code, CancellationToken cancellationToken = default) =>
        settings.SetAsync(RecoveryKey, Convert.ToBase64String(ring.Protect(System.Text.Encoding.ASCII.GetBytes(code.Normalized), "device/recovery")), cancellationToken);

    /// <summary>The recovery code, shown again in Settings after the app is unlocked.</summary>
    public async Task<RecoveryCode?> LoadRecoveryCodeAsync(CancellationToken cancellationToken = default)
    {
        var stored = await settings.GetAsync(RecoveryKey, cancellationToken);
        if (stored is null || ring.Unprotect(Convert.FromBase64String(stored), "device/recovery") is not { } bytes)
        {
            return null;
        }

        return RecoveryCode.TryParse(System.Text.Encoding.ASCII.GetString(bytes), out var code) ? code : null;
    }
}
