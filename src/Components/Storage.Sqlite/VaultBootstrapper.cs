using TravelVault.Core.Backup;
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

    public string ViewCacheFolder => Path.Combine(Folder, "view-cache");

    public void Dispose()
    {
        Database.Dispose();
        Session.KeyRing.Dispose();
        Session.DeviceKeys.Dispose();
    }
}

/// <summary>
/// First launch, every launch and restore. The only thing on disk outside the encrypted database is the device
/// master key wrapped by the platform key vault (Android Keystore) in <c>dmk.bin</c>.
/// </summary>
public sealed class VaultBootstrapper(string folder, IKeyVault keyVault, IClock clock)
{
    private string DmkPath => Path.Combine(folder, "dmk.bin");

    private string DbPath => Path.Combine(folder, "vault.db");

    public bool IsSetUp => File.Exists(DmkPath) && File.Exists(DbPath);

    /// <summary>New phone: master key, database, device keys and recovery code (shown once to the user).</summary>
    public async Task<(OpenedVault Vault, RecoveryCode Code)> CreateAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var name = displayName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Please enter your name.", nameof(displayName));
        }

        var keys = DeviceKeyPair.Create();
        var (signing, agreement) = keys.ExportPrivate();
        var code = RecoveryCode.Generate();
        var vault = await CreateVaultAsync(new DeviceIdentity(Ids.New(), name, signing, agreement), cancellationToken);
        await new DeviceIdentityStore(new SqliteSettings(vault.Database), vault.Session.KeyRing).SaveRecoveryCodeAsync(code, cancellationToken);
        return (vault, code);
    }

    /// <returns>The vault, or null if the master key can't be unwrapped (e.g. the Keystore key was lost): offer restore.</returns>
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

    /// <summary>
    /// Restores a backup onto this phone (replacing anything here). The phone continues as the same family member
    /// (same member id and device keys), so family phones keep trusting it; then a sync fills in anything newer.
    /// </summary>
    /// <exception cref="WrongRecoveryCodeException">Wrong code.</exception>
    public async Task<OpenedVault> RestoreAsync(Stream backupFile, RecoveryCode code, CancellationToken cancellationToken = default)
    {
        using var backup = await BackupFormat.OpenAsync(backupFile, code, cancellationToken);
        var m = backup.Manifest;
        Wipe();
        var vault = await CreateVaultAsync(new DeviceIdentity(m.MemberId, m.DisplayName, m.SigningPrivateKey, m.AgreementPrivateKey), cancellationToken);
        var settings = new SqliteSettings(vault.Database);
        var identity = new DeviceIdentityStore(settings, vault.Session.KeyRing);
        await identity.SaveRecoveryCodeAsync(code, cancellationToken);

        var documents = new SqliteDocumentRepository(vault.Database);
        var travelers = new SqliteTravelerRepository(vault.Database);
        var fileKeys = new SqliteFileKeyStore(vault.Database);
        var search = new SearchService(new SqliteSearchIndex(vault.Database), documents, travelers);
        var documentService = new DocumentService(vault.Session, documents, new FileVault(vault.FilesFolder), fileKeys, NoRequests.Instance, new VaultEvents([]));
        var service = new BackupService(vault.Session, new SqliteFamilyRepository(vault.Database), travelers, documents, new SqliteTripRepository(vault.Database),
            new SqliteSyncStore(vault.Database), settings, documentService, identity);
        await service.ImportAsync(backup, search, cancellationToken);
        return vault;
    }

    /// <summary>"Erase everything": destroys the Keystore key (all data becomes unreadable at once), then deletes the files.</summary>
    public void EraseEverything()
    {
        keyVault.Destroy();
        Wipe();
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

    private void Wipe()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
        {
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
    }

    private sealed class NoRequests : IFileRequester
    {
        public static readonly NoRequests Instance = new();

        public Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
