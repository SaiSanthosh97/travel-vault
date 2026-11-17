using TravelVault.Core.Backup;
using TravelVault.Core.Common;
using TravelVault.Core.Families;
using TravelVault.Core.Parsing;
using TravelVault.Core.Security;
using TravelVault.Core.Sync;
using TravelVault.Core.Vault;
using TravelVault.Storage.Sqlite;

namespace TravelVault.Core.Backup.Tests;

public sealed class BackupAndBootstrapTests : IDisposable
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly List<string> _folders = [];

    private string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tv-boot-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        return folder;
    }

    /// <summary>The services the app would build on an opened vault (only what these tests need).</summary>
    private sealed record Services(OpenedVault Vault, ImportService Import, DocumentService Documents, SearchService Search, BackupService Backup, SqliteFamilyRepository Families);

    private static Services Wire(OpenedVault vault)
    {
        var db = vault.Database;
        var documents = new SqliteDocumentRepository(db);
        var travelers = new SqliteTravelerRepository(db);
        var settings = new SqliteSettings(db);
        var fileKeys = new SqliteFileKeyStore(db);
        var files = new FileVault(vault.FilesFolder);
        var search = new SearchService(new SqliteSearchIndex(db), documents, travelers);
        var events = new VaultEvents([search]);
        var travelerService = new TravelerService(vault.Session, travelers, events);
        var documentService = new DocumentService(vault.Session, documents, files, fileKeys, new NoRequests(), events);
        var import = new ImportService(vault.Session, new ContentExtractor(new NoOcr(), new NoBarcodes(), new NoPages()), documents, travelers, files, fileKeys, travelerService, events);
        var families = new SqliteFamilyRepository(db);
        var backup = new BackupService(vault.Session, families, travelers, documents, new SqliteTripRepository(db), new SqliteSyncStore(db), settings, documentService,
            new DeviceIdentityStore(settings, vault.Session.KeyRing));
        return new Services(vault, import, documentService, search, backup, families);
    }

    [Fact]
    public async Task Create_then_unlock_gives_the_same_identity_and_erase_makes_it_unreadable()
    {
        var keyVault = new InMemoryKeyVault();
        var folder = NewFolder();
        var boot = new VaultBootstrapper(folder, keyVault, _clock);
        Assert.False(boot.IsSetUp);

        var (created, code) = await boot.CreateAsync("Rahul");
        var memberId = created.Session.MemberId;
        var signing = created.Session.DeviceKeys.Public.Signing;
        created.Dispose();

        Assert.True(boot.IsSetUp);
        using (var unlocked = (await boot.UnlockAsync())!)
        {
            Assert.Equal(memberId, unlocked.Session.MemberId);
            Assert.Equal("Rahul", unlocked.Session.DisplayName);
            Assert.Equal(signing, unlocked.Session.DeviceKeys.Public.Signing);
            var stored = await new DeviceIdentityStore(new SqliteSettings(unlocked.Database), unlocked.Session.KeyRing).LoadRecoveryCodeAsync();
            Assert.Equal(code.Normalized, stored!.Normalized);
        }

        boot.EraseEverything();
        Assert.Null(await boot.UnlockAsync());
    }

    [Fact]
    public async Task A_different_key_vault_cannot_unlock_the_files()
    {
        var folder = NewFolder();
        var (vault, _) = await new VaultBootstrapper(folder, new InMemoryKeyVault(), _clock).CreateAsync("Rahul");
        vault.Dispose();
        Assert.Null(await new VaultBootstrapper(folder, new InMemoryKeyVault(), _clock).UnlockAsync());
    }

    [Fact]
    public async Task Backup_restores_everything_on_a_new_phone_as_the_same_member()
    {
        var (oldVault, code) = await new VaultBootstrapper(NewFolder(), new InMemoryKeyVault(), _clock).CreateAsync("Rahul Sharma");
        var oldPhone = Wire(oldVault);

        var family = Family.Create("Sharma Family", new Member
        {
            MemberId = oldVault.Session.MemberId,
            DisplayName = "Rahul Sharma",
            SigningPublicKey = oldVault.Session.DeviceKeys.Public.Signing,
            AgreementPublicKey = oldVault.Session.DeviceKeys.Public.Agreement,
        }, _clock.UtcNow);
        oldVault.Session.Stamp(family);
        await oldPhone.Families.SaveAsync(family);
        var familyKey = AeadBox.NewKey();
        await oldPhone.Families.SaveKeyAsync(family.Id, 1, oldVault.Session.KeyRing.Protect(familyKey, $"family/{family.Id}/1"));
        await new SqliteSettings(oldVault.Database).SetAsync($"family.{family.Id}.seq", "41");

        var bytes = await File.ReadAllBytesAsync(Path.Combine("samples", "flight-eticket-domestic.txt"));
        var ticket = await oldPhone.Import.SaveAsync((await oldPhone.Import.PrepareAsync(family.Id, bytes, null)).Draft!);

        using var backupFile = new MemoryStream();
        await oldPhone.Backup.ExportAsync(backupFile, iterations: 1000);
        Assert.NotNull(await oldPhone.Backup.LastBackupAsync());
        Assert.DoesNotContain("X7KQ2P", System.Text.Encoding.UTF8.GetString(backupFile.ToArray()));
        var oldMember = oldVault.Session.MemberId;
        oldVault.Dispose();

        backupFile.Position = 0;
        var newKeyVault = new InMemoryKeyVault(); // a new phone has its own Keystore
        using var restored = await new VaultBootstrapper(NewFolder(), newKeyVault, _clock).RestoreAsync(backupFile, code);
        var newPhone = Wire(restored);

        Assert.Equal(oldMember, restored.Session.MemberId);
        var doc = (await newPhone.Documents.GetAsync(ticket.Id))!;
        Assert.Equal("X7KQ2P", doc.Pnr);
        Assert.Equal(bytes, (await newPhone.Documents.OpenFileAsync(ticket.Id)).Content);
        Assert.Single(await newPhone.Search.SearchAsync(family.Id, "priya"));
        var restoredKey = restored.Session.KeyRing.Unprotect((await newPhone.Families.GetKeyAsync(family.Id, 1))!, $"family/{family.Id}/1");
        Assert.Equal(familyKey, restoredKey);
        Assert.Equal((41 + BackupService.SeqSafetyGap).ToString(System.Globalization.CultureInfo.InvariantCulture),
            await new SqliteSettings(restored.Database).GetAsync($"family.{family.Id}.seq"));
    }

    [Fact]
    public async Task A_wrong_recovery_code_or_a_random_file_is_refused()
    {
        var (vault, _) = await new VaultBootstrapper(NewFolder(), new InMemoryKeyVault(), _clock).CreateAsync("Rahul");
        var phone = Wire(vault);
        using var backupFile = new MemoryStream();
        await phone.Backup.ExportAsync(backupFile, iterations: 1000);
        vault.Dispose();

        backupFile.Position = 0;
        await Assert.ThrowsAsync<WrongRecoveryCodeException>(() => BackupFormat.OpenAsync(backupFile, RecoveryCode.Generate()));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupFormat.OpenAsync(new MemoryStream([1, 2, 3, 4]), RecoveryCode.Generate()));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var folder in _folders.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NoRequests : IFileRequester
    {
        public Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOcr : ITextRecognizer
    {
        public Task<string> ReadTextAsync(byte[] image, CancellationToken cancellationToken = default) => Task.FromResult("");
    }

    private sealed class NoBarcodes : IBarcodeScanner
    {
        public Task<IReadOnlyList<Barcode>> ScanAsync(byte[] image, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Barcode>>([]);
    }

    private sealed class NoPages : IPdfRasterizer
    {
        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(byte[] pdf, int maxPages, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<byte[]>>([]);
    }
}
