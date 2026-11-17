using TravelVault.Core.Security;

namespace TravelVault.Core.Vault;

/// <summary>This phone's identity: member id, name and private keys (wrapped by the key ring).</summary>
public sealed record DeviceIdentity(string MemberId, string DisplayName, byte[] SigningPrivateKey, byte[] AgreementPrivateKey)
{
    public DeviceKeyPair ToKeyPair() => DeviceKeyPair.Import(SigningPrivateKey, AgreementPrivateKey);
}

/// <summary>Stores the device identity in the encrypted settings, the private keys additionally wrapped by the key ring.</summary>
public sealed class DeviceIdentityStore(ISettingsStore settings, KeyRing ring)
{
    private const string MemberIdKey = "device.memberId";
    private const string NameKey = "device.name";
    private const string SigningKey = "device.signing";
    private const string AgreementKey = "device.agreement";

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
}
