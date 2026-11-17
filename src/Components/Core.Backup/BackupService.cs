using System.Globalization;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Security;
using TravelVault.Core.Sync;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Backup;

/// <summary>
/// Export: gathers this phone's whole vault (all families, keys, records, change logs and original files) into one
/// encrypted file. Import: writes an opened backup into a freshly created vault on a new phone, re-encrypting every
/// secret under the new phone's key ring.
/// </summary>
public sealed class BackupService(
    VaultSession session,
    IFamilyRepository families,
    ITravelerRepository travelers,
    IDocumentRepository documents,
    ITripRepository trips,
    ISyncStore sync,
    ISettingsStore settings,
    DocumentService documentService,
    DeviceIdentityStore identity)
{
    public const string LastBackupKey = "backup.last";

    /// <summary>Restored phones skip ahead in their message numbering, in case the old phone sent messages after the backup.</summary>
    public const long SeqSafetyGap = 100_000;

    public async Task ExportAsync(Stream output, int iterations = BackupKey.DefaultIterations, CancellationToken cancellationToken = default)
    {
        var code = await identity.LoadRecoveryCodeAsync(cancellationToken) ?? throw new InvalidOperationException("No recovery code on this phone.");
        var device = await identity.LoadAsync(cancellationToken) ?? throw new InvalidOperationException("No device identity on this phone.");
        var manifest = new BackupManifest
        {
            CreatedAt = session.Now,
            MemberId = device.MemberId,
            DisplayName = device.DisplayName,
            SigningPrivateKey = device.SigningPrivateKey,
            AgreementPrivateKey = device.AgreementPrivateKey,
        };

        var fileOwners = new Dictionary<string, string>();
        foreach (var family in await families.ListAsync(cancellationToken))
        {
            var keys = new Dictionary<int, byte[]>();
            foreach (var (epoch, wrapped) in await families.GetAllKeysAsync(family.Id, cancellationToken))
            {
                if (session.KeyRing.Unprotect(wrapped, $"family/{family.Id}/{epoch}") is { } key)
                {
                    keys[epoch] = key;
                }
            }

            var docs = (await documents.ListAsync(family.Id, cancellationToken)).Concat(await documents.ListDeletedAsync(family.Id, cancellationToken)).ToList();
            foreach (var file in docs.SelectMany(d => d.Files))
            {
                fileOwners[file.Id] = family.Id;
            }

            manifest.Families.Add(new FamilyBackup
            {
                Family = family,
                Keys = keys,
                Travelers = [.. await travelers.ListAsync(family.Id, includeDeleted: true, cancellationToken)],
                Documents = docs,
                Trips = [.. await trips.ListAsync(family.Id, cancellationToken)],
                Changes = [.. await sync.ListChangesAsync(family.Id, cancellationToken)],
                MySeq = long.TryParse(await settings.GetAsync($"family.{family.Id}.seq", cancellationToken), out var seq) ? seq : 0,
                Removed = await settings.GetAsync($"family.{family.Id}.removed", cancellationToken) == "1",
            });
        }

        await BackupFormat.WriteAsync(output, manifest, fileOwners.Keys, id => documentService.ReadLocalFileAsync(fileOwners[id], id, cancellationToken), code, iterations, cancellationToken);
        await settings.SetAsync(LastBackupKey, session.Now.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task<DateTimeOffset?> LastBackupAsync(CancellationToken cancellationToken = default) =>
        DateTimeOffset.TryParse(await settings.GetAsync(LastBackupKey, cancellationToken), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    /// <summary>
    /// Writes a backup into this (new, empty) vault. The session must already use the backup's member id and keys
    /// (the bootstrapper creates it from the manifest), so the restored phone continues as the same family member.
    /// </summary>
    public async Task ImportAsync(OpenedBackup backup, IVaultChangeHandler? searchIndexer = null, CancellationToken cancellationToken = default)
    {
        foreach (var part in backup.Manifest.Families)
        {
            var family = part.Family;
            foreach (var (epoch, key) in part.Keys)
            {
                await families.SaveKeyAsync(family.Id, epoch, session.KeyRing.Protect(key, $"family/{family.Id}/{epoch}"), cancellationToken);
            }

            await families.SaveAsync(family, cancellationToken);
            foreach (var traveler in part.Travelers)
            {
                await travelers.SaveAsync(traveler, cancellationToken);
            }

            foreach (var trip in part.Trips)
            {
                await trips.SaveAsync(trip, cancellationToken);
            }

            foreach (var doc in part.Documents)
            {
                await documents.SaveAsync(doc, cancellationToken);
                foreach (var file in doc.Files)
                {
                    if (await backup.ReadFileAsync(file.Id) is { } content)
                    {
                        await documentService.StoreReceivedFileAsync(family.Id, file.Id, content, cancellationToken);
                    }
                }

                if (searchIndexer is not null)
                {
                    await searchIndexer.HandleAsync(new VaultChange(family.Id, RecordType.Document, doc.Id, FromSync: true), cancellationToken);
                }
            }

            foreach (var change in part.Changes)
            {
                await sync.SaveChangeAsync(change, cancellationToken);
            }

            await settings.SetAsync($"family.{family.Id}.seq", (part.MySeq + SeqSafetyGap).ToString(CultureInfo.InvariantCulture), cancellationToken);
            if (part.Removed)
            {
                await settings.SetAsync($"family.{family.Id}.removed", "1", cancellationToken);
            }
        }
    }
}
