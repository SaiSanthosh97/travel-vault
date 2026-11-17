using TravelVault.Core.Parsing;
using TravelVault.Core.Relay;
using TravelVault.Core.Security;
using TravelVault.Core.Sync;
using TravelVault.Core.Vault;
using TravelVault.Device.Vision;
using TravelVault.Storage.Sqlite;

namespace TravelVault.App.Services;

/// <summary>
/// Composition root for an unlocked vault: builds every component on top of the session and encrypted database,
/// exactly as the tests wire them (tests/Core.Sync.Tests/Phone.cs). Created when the vault opens, disposed when it closes.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(OpenedVault vault, HttpClient http, IVaultChangeHandler uiHandler)
    {
        Vault = vault;
        var db = vault.Database;
        Documents = new SqliteDocumentRepository(db);
        TravelerRepository = new SqliteTravelerRepository(db);
        Trips = new SqliteTripRepository(db);
        Families = new SqliteFamilyRepository(db);
        Settings = new SqliteSettings(db);
        FileKeys = new SqliteFileKeyStore(db);
        Files = new FileVault(vault.FilesFolder);
        SyncStore = new SqliteSyncStore(db);
        Identity = new DeviceIdentityStore(Settings, vault.Session.KeyRing);

        Search = new SearchService(new SqliteSearchIndex(db), Documents, TravelerRepository);
        Events = new VaultEvents([Search]);

        var requester = new LateFileRequester();
        Travelers = new TravelerService(Session, TravelerRepository, Events);
        DocumentService = new DocumentService(Session, Documents, Files, FileKeys, requester, Events);
        Rasterizer = new AndroidPdfRasterizer();
        BarcodeScanner = new MlKitBarcodeScanner();
        Import = new ImportService(Session, new ContentExtractor(new MlKitTextRecognizer(), BarcodeScanner, Rasterizer), Documents, TravelerRepository, Files, FileKeys, Travelers, Events);
        TripService = new TripService(Session, Trips, Documents, Settings, Events);
        Sync = new FamilySync(Session, Families, Documents, TravelerRepository, Trips, DocumentService, Travelers, FileKeys, Files, SyncStore, Settings,
            new NtfyRelayFactory(http, "TravelVault"), Events, vault.IncomingFolder);
        requester.Target = Sync;

        Events.Add(Sync);      // signs and queues local changes
        Events.Add(uiHandler); // refreshes screens, re-plans reminders, sends soon
    }

    public OpenedVault Vault { get; }

    public VaultSession Session => Vault.Session;

    public SqliteDocumentRepository Documents { get; }

    public SqliteTravelerRepository TravelerRepository { get; }

    public SqliteTripRepository Trips { get; }

    public SqliteFamilyRepository Families { get; }

    public SqliteSettings Settings { get; }

    public SqliteFileKeyStore FileKeys { get; }

    public FileVault Files { get; }

    public SqliteSyncStore SyncStore { get; }

    public DeviceIdentityStore Identity { get; }

    public SearchService Search { get; }

    public VaultEvents Events { get; }

    public TravelerService Travelers { get; }

    public DocumentService DocumentService { get; }

    public AndroidPdfRasterizer Rasterizer { get; }

    public MlKitBarcodeScanner BarcodeScanner { get; }

    public ImportService Import { get; }

    public TripService TripService { get; }

    public FamilySync Sync { get; }

    public void Dispose() => Vault.Dispose();

    /// <summary>DocumentService needs a file requester and sync needs DocumentService: this breaks the cycle.</summary>
    private sealed class LateFileRequester : IFileRequester
    {
        public IFileRequester? Target { get; set; }

        public Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default) =>
            Target?.RequestAsync(familyId, fileVersionId, cancellationToken) ?? Task.CompletedTask;
    }
}
