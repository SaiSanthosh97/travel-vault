using System.Text;
using TravelVault.Core.Common;
using TravelVault.Core.Parsing;
using TravelVault.Core.Relay;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;
using TravelVault.Storage.Sqlite;

namespace TravelVault.Core.Sync.Tests;

/// <summary>One simulated phone: its own encrypted database, file vault, keys and services, wired like the app.</summary>
internal sealed class Phone : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tv-phone-" + Guid.NewGuid().ToString("N"));

    public Phone(string name, ManualClock clock, IRelayFactory relay, SyncOptions? options = null)
    {
        Name = name;
        Directory.CreateDirectory(_folder);
        var (ring, _) = KeyRing.CreateNew(new InMemoryKeyVault());
        Session = new VaultSession(Ids.New(), name, ring, DeviceKeyPair.Create(), clock);
        Database = VaultDatabase.Open(Path.Combine(_folder, "vault.db"), ring.DatabaseKey());

        Documents = new SqliteDocumentRepository(Database);
        var travelerRepo = new SqliteTravelerRepository(Database);
        var trips = new SqliteTripRepository(Database);
        Families = new SqliteFamilyRepository(Database);
        var settings = new SqliteSettings(Database);
        var fileKeys = new SqliteFileKeyStore(Database);
        var files = new FileVault(Path.Combine(_folder, "files"));
        Search = new SearchService(new SqliteSearchIndex(Database), Documents, travelerRepo);
        Events = new VaultEvents([Search]);
        Events.HandlerFailed += ex => throw new InvalidOperationException("Handler failed", ex);

        Travelers = new TravelerService(Session, travelerRepo, Events);
        var requester = new DeferredRequester();
        DocumentService = new DocumentService(Session, Documents, files, fileKeys, requester, Events);
        Import = new ImportService(Session, new ContentExtractor(new NoOcr(), new NoBarcodes(), new NoPages()), Documents, travelerRepo, files, fileKeys, Travelers, Events);
        Sync = new FamilySync(Session, Families, Documents, travelerRepo, trips, DocumentService, Travelers, fileKeys, files,
            new SqliteSyncStore(Database), settings, relay, Events, Path.Combine(_folder, "incoming"), options);
        requester.Target = Sync;
        Events.Add(Sync);
        Sync.Notice += (_, message) => Notices.Add(message);
        Sync.Failed += ex => Failures.Add(ex);
    }

    public string Name { get; }

    public VaultSession Session { get; }

    public VaultDatabase Database { get; }

    public SqliteDocumentRepository Documents { get; }

    public SqliteFamilyRepository Families { get; }

    public SearchService Search { get; }

    public VaultEvents Events { get; }

    public TravelerService Travelers { get; }

    public DocumentService DocumentService { get; }

    public ImportService Import { get; }

    public FamilySync Sync { get; }

    public List<string> Notices { get; } = [];

    public List<Exception> Failures { get; } = [];

    public string Id => Session.MemberId;

    public async Task<Core.Documents.TravelDocument> ImportAsync(string familyId, string sampleOrText)
    {
        var bytes = File.Exists(Path.Combine("samples", sampleOrText))
            ? await File.ReadAllBytesAsync(Path.Combine("samples", sampleOrText))
            : Encoding.UTF8.GetBytes(sampleOrText);
        var result = await Import.PrepareAsync(familyId, bytes, null);
        return await Import.SaveAsync(result.Draft ?? throw new InvalidOperationException(result.Status.ToString()));
    }

    public Task SyncAsync() => Sync.SyncAllAsync();

    public void Dispose()
    {
        Database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class DeferredRequester : IFileRequester
    {
        public IFileRequester? Target { get; set; }

        public Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default) =>
            Target?.RequestAsync(familyId, fileVersionId, cancellationToken) ?? Task.CompletedTask;
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
