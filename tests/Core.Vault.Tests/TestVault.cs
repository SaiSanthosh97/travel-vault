using System.Text;
using TravelVault.Core.Common;
using TravelVault.Core.Parsing;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;
using TravelVault.Storage.Sqlite;

namespace TravelVault.Core.Vault.Tests;

/// <summary>A complete vault on disk (temp folder) with fake device readers, wired like the app wires it.</summary>
internal sealed class TestVault : IDisposable
{
    public const string FamilyId = "family0000000000000000000000001";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tv-vault-" + Guid.NewGuid().ToString("N"));

    public TestVault(DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(_folder);
        Clock = new ManualClock(now ?? new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var (ring, _) = KeyRing.CreateNew(new InMemoryKeyVault());
        Session = new VaultSession("member-me", "Rahul Sharma", ring, DeviceKeyPair.Create(), Clock);
        Database = VaultDatabase.Open(Path.Combine(_folder, "vault.db"), ring.DatabaseKey());

        Documents = new SqliteDocumentRepository(Database);
        TravelerRepo = new SqliteTravelerRepository(Database);
        Settings = new SqliteSettings(Database);
        FileKeys = new SqliteFileKeyStore(Database);
        Files = new FileVault(Path.Combine(_folder, "files"));
        Search = new SearchService(new SqliteSearchIndex(Database), Documents, TravelerRepo);
        Events = new VaultEvents([Search]);
        Events.HandlerFailed += ex => throw ex;

        Travelers = new TravelerService(Session, TravelerRepo, Events);
        DocumentService = new DocumentService(Session, Documents, Files, FileKeys, Requester, Events);
        Trips = new TripService(Session, new SqliteTripRepository(Database), Documents, Settings, Events);
        Import = new ImportService(Session, new ContentExtractor(Ocr, Scanner, new NoPdfPages()), Documents, TravelerRepo, Files, FileKeys, Travelers, Events);
    }

    public ManualClock Clock { get; }

    public VaultSession Session { get; }

    public VaultDatabase Database { get; }

    public SqliteDocumentRepository Documents { get; }

    public SqliteTravelerRepository TravelerRepo { get; }

    public SqliteSettings Settings { get; }

    public SqliteFileKeyStore FileKeys { get; }

    public FileVault Files { get; }

    public SearchService Search { get; }

    public VaultEvents Events { get; }

    public TravelerService Travelers { get; }

    public DocumentService DocumentService { get; }

    public TripService Trips { get; }

    public ImportService Import { get; }

    public FakeOcr Ocr { get; } = new();

    public FakeScanner Scanner { get; } = new();

    public RecordingRequester Requester { get; } = new();

    public static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine("samples", name));

    /// <summary>A fake "photo": PNG magic bytes + an id, which the fake OCR maps to text.</summary>
    public byte[] Photo(string id, string ocrText, params Barcode[] barcodes)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.UTF8.GetBytes(id)];
        Ocr.Texts[Convert.ToBase64String(bytes)] = ocrText;
        Scanner.Codes[Convert.ToBase64String(bytes)] = barcodes;
        return bytes;
    }

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

    internal sealed class FakeOcr : ITextRecognizer
    {
        public Dictionary<string, string> Texts { get; } = [];

        public Task<string> ReadTextAsync(byte[] image, CancellationToken cancellationToken = default) =>
            Task.FromResult(Texts.GetValueOrDefault(Convert.ToBase64String(image), ""));
    }

    internal sealed class FakeScanner : IBarcodeScanner
    {
        public Dictionary<string, Barcode[]> Codes { get; } = [];

        public Task<IReadOnlyList<Barcode>> ScanAsync(byte[] image, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Barcode>>(Codes.GetValueOrDefault(Convert.ToBase64String(image), []));
    }

    internal sealed class NoPdfPages : IPdfRasterizer
    {
        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(byte[] pdf, int maxPages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<byte[]>>([]);
    }

    internal sealed class RecordingRequester : IFileRequester
    {
        public List<string> Requested { get; } = [];

        public Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default)
        {
            Requested.Add(fileVersionId);
            return Task.CompletedTask;
        }
    }
}
