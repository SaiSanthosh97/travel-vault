using TravelVault.Core.Common;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;

namespace TravelVault.Storage.Sqlite;

/// <summary>An unlocked vault: who this phone is, its database, and where its files live.</summary>
public sealed class OpenedVault(VaultSession session, VaultDatabase database, string folder) : IDisposable
{
    public VaultSession Session { get; } = session;

    public VaultDatabase Database { get; } = database;

    public string Folder { get; } = folder;

    public string FilesFolder => Path.Combine(Folder, "files");

    public string IncomingFolder => Path.Combine(Folder, "incoming");

    public void Dispose()
    {
        Database.Dispose();
        Session.KeyRing.Dispose();
        Session.DeviceKeys.Dispose();
    }
}

/// <summary>
/// First launch, every launch and "erase everything". The only thing on disk outside the encrypted database is the
/// device master key wrapped by the platform key vault (Android Keystore) in <c>dmk.bin</c>.
/// There is no backup/restore (C-020): a new phone gets its data back by rejoining the family.
/// </summary>
public sealed class VaultBootstrapper(string folder, IKeyVault keyVault, IClock clock)
{
    private string DmkPath => Path.Combine(folder, "dmk.bin");

    private string DbPath => Path.Combine(folder, "vault.db");

    public bool IsSetUp => File.Exists(DmkPath) && File.Exists(DbPath);

    /// <summary>New phone: master key, database and device keys.</summary>
    public async Task<OpenedVault> CreateAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var name = displayName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Please enter your name.", nameof(displayName));
        }

        using var keys = DeviceKeyPair.Create();
        var (signing, agreement) = keys.ExportPrivate();
        return await CreateVaultAsync(new DeviceIdentity(Ids.New(), name, signing, agreement), cancellationToken);
    }

    /// <returns>The vault, or null if the master key can't be unwrapped (e.g. the Keystore key was lost): offer to start over.</returns>
    public async Task<OpenedVault?> UnlockAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSetUp || KeyRing.Unlock(keyVault, await File.ReadAllBytesAsync(DmkPath, cancellationToken)) is not { } ring)
        {
            return null;
        }

        VaultDatabase db;
        try
        {
            db = VaultDatabase.Open(DbPath, ring.DatabaseKey());
        }
        catch (WrongKeyException)
        {
            ring.Dispose();
            return null;
        }

        var identity = await new DeviceIdentityStore(new SqliteSettings(db), ring).LoadAsync(cancellationToken);
        if (identity is null)
        {
            db.Dispose();
            ring.Dispose();
            return null;
        }

        return new OpenedVault(new VaultSession(identity.MemberId, identity.DisplayName, ring, identity.ToKeyPair(), clock), db, folder);
    }

    /// <summary>"Erase everything": destroys the Keystore key (all data becomes unreadable at once), then deletes the files.</summary>
    public void EraseEverything()
    {
        keyVault.Destroy();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private async Task<OpenedVault> CreateVaultAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var (ring, wrapped) = KeyRing.CreateNew(keyVault);
        await File.WriteAllBytesAsync(DmkPath, wrapped, cancellationToken);
        var db = VaultDatabase.Open(DbPath, ring.DatabaseKey());
        await new DeviceIdentityStore(new SqliteSettings(db), ring).SaveAsync(identity, cancellationToken);
        return new OpenedVault(new VaultSession(identity.MemberId, identity.DisplayName, ring, identity.ToKeyPair(), clock), db, folder);
    }
}
