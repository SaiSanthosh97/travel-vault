using System.Text;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Security;
using TravelVault.Core.Travelers;
using TravelVault.Storage.Sqlite;

namespace TravelVault.Storage.Sqlite.Tests;

public sealed class VaultDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tv-db-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = AeadBox.NewKey();

    public VaultDatabaseTests() => Directory.CreateDirectory(_folder);

    private string DbPath => Path.Combine(_folder, "vault.db");

    [Fact]
    public async Task Database_file_is_encrypted_on_disk()
    {
        using (var db = VaultDatabase.Open(DbPath, _key))
        {
            var doc = Ticket("fam", "X7KQ2P");
            doc.Notes = "SECRET-NOTE-12345";
            await new SqliteDocumentRepository(db).SaveAsync(doc);
        }

        var bytes = await File.ReadAllBytesAsync(DbPath);
        var text = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("SQLite format 3", text);
        Assert.DoesNotContain("SECRET-NOTE", text);
        Assert.DoesNotContain("X7KQ2P", text);
    }

    [Fact]
    public async Task Wrong_key_is_rejected_and_right_key_reopens()
    {
        using (var db = VaultDatabase.Open(DbPath, _key))
        {
            await new SqliteDocumentRepository(db).SaveAsync(Ticket("fam", "X7KQ2P"));
        }

        Assert.Throws<WrongKeyException>(() => VaultDatabase.Open(DbPath, AeadBox.NewKey()));
        using var reopened = VaultDatabase.Open(DbPath, _key);
        Assert.Single(await new SqliteDocumentRepository(reopened).ListAsync("fam"));
    }

    [Fact]
    public async Task Full_text_search_is_available_in_the_encrypted_build()
    {
        using var db = VaultDatabase.Open(DbPath, _key);
        var hits = await db.UseAsync(c =>
        {
            using var insert = c.CreateCommand();
            insert.CommandText = "INSERT INTO search_index (document_id, family_id, title, places) VALUES ('d1', 'fam', 'Flight', 'Delhi DEL')";
            insert.ExecuteNonQuery();
            using var query = c.CreateCommand();
            query.CommandText = "SELECT document_id FROM search_index WHERE search_index MATCH 'del*'";
            return query.ExecuteScalar() as string;
        });
        Assert.Equal("d1", hits);
    }

    [Fact]
    public async Task Documents_round_trip_with_all_details()
    {
        using var db = VaultDatabase.Open(DbPath, _key);
        var repo = new SqliteDocumentRepository(db);
        var doc = Ticket("fam", "x7kq-2p");
        doc.Identity = new IdentityInfo("Z1234567", "IN", null, new DateOnly(2030, 1, 1));
        doc.Version = new HybridTimestamp(123, 4, "node");
        doc.MarkEdited(TravelDocument.Fields.Pnr);
        await repo.SaveAsync(doc);

        var loaded = (await repo.GetAsync(doc.Id))!;
        Assert.Equal(doc.Version, loaded.Version);
        Assert.Equal("Delhi", loaded.Segments[0].To!.City);
        Assert.Equal(doc.Segments[0].DepartsUtc, loaded.Segments[0].DepartsUtc);
        Assert.Equal(Confidence.UserEdited, loaded.FieldConfidence[TravelDocument.Fields.Pnr]);
        Assert.Equal(new DateOnly(2030, 1, 1), loaded.Identity!.ExpiresOn);
        Assert.Single(await repo.FindByReferenceAsync("fam", "X7KQ2P"));
        Assert.Empty(await repo.FindByReferenceAsync("other-family", "X7KQ2P"));
    }

    [Fact]
    public async Task Exact_duplicate_lookup_by_file_hash_ignores_deleted_documents()
    {
        using var db = VaultDatabase.Open(DbPath, _key);
        var repo = new SqliteDocumentRepository(db);
        var doc = Ticket("fam", "X7KQ2P");
        await repo.SaveAsync(doc);
        Assert.Equal(doc.Id, (await repo.FindByFileHashAsync("fam", "ABCDEF"))!.Id);

        doc.Delete(Now);
        await repo.SaveAsync(doc);
        Assert.Null(await repo.FindByFileHashAsync("fam", "abcdef"));
        Assert.Single(await repo.ListDeletedAsync("fam"));
        Assert.Equal(1, await repo.PurgeDeletedAsync(Now.AddDays(1)));
    }

    [Fact]
    public async Task Families_keys_and_travelers_round_trip_and_family_wipe_removes_everything()
    {
        using var db = VaultDatabase.Open(DbPath, _key);
        var families = new SqliteFamilyRepository(db);
        using var device = DeviceKeyPair.Create();
        var family = Family.Create("Sharma Family", new Member
        {
            MemberId = "me",
            DisplayName = "Me",
            SigningPublicKey = device.Public.Signing,
            AgreementPublicKey = device.Public.Agreement,
        }, Now);
        await families.SaveAsync(family);
        await families.SaveKeyAsync(family.Id, 1, [1, 2, 3]);
        var travelers = new SqliteTravelerRepository(db);
        await travelers.SaveAsync(Traveler.Create(family.Id, "Rahul"));
        await new SqliteDocumentRepository(db).SaveAsync(Ticket(family.Id, "X7KQ2P"));

        var loaded = (await families.GetAsync(family.Id))!;
        Assert.Equal(family.RelayTopic, loaded.RelayTopic);
        Assert.Equal(device.Public.Signing, loaded.Members[0].SigningPublicKey);
        Assert.Equal([1, 2, 3], await families.GetKeyAsync(family.Id, 1));
        Assert.Single(await travelers.ListAsync(family.Id));

        await families.DeleteEverythingAsync(family.Id);
        Assert.Null(await families.GetAsync(family.Id));
        Assert.Null(await families.GetKeyAsync(family.Id, 1));
        Assert.Empty(await travelers.ListAsync(family.Id));
        Assert.Empty(await new SqliteDocumentRepository(db).ListAsync(family.Id));
    }

    [Fact]
    public async Task Settings_and_file_keys()
    {
        using var db = VaultDatabase.Open(DbPath, _key);
        var settings = new SqliteSettings(db);
        await settings.SetAsync("a", "1");
        Assert.Equal("1", await settings.GetAsync("a"));
        await settings.SetAsync("a", null);
        Assert.Null(await settings.GetAsync("a"));

        var keys = new SqliteFileKeyStore(db);
        await keys.SaveAsync("fv1", "fam", [9], Now);
        Assert.Contains("fv1", await keys.PresentAsync("fam"));
        Assert.Equal([9], await keys.GetAsync("fv1"));
    }

    private static TravelDocument Ticket(string familyId, string pnr)
    {
        var doc = TravelDocument.Create(familyId, DocumentType.Flight, "me", Now);
        doc.Pnr = pnr;
        doc.Segments.Add(new Segment
        {
            Id = Ids.New(),
            Mode = TravelMode.Flight,
            From = new Place("Kempegowda Intl", "BLR", "Bengaluru", "IN", "Asia/Kolkata"),
            To = new Place("Indira Gandhi Intl", "DEL", "Delhi", "IN", "Asia/Kolkata"),
            Departs = new LocalDateTimeZ(new DateTime(2026, 12, 12, 6, 10, 0), "Asia/Kolkata"),
        });
        doc.SetPassengers([], "traveler-me");
        doc.AddFile(new FileVersion { Id = Ids.New(), Sha256 = "abcdef", MediaType = "application/pdf", AddedAt = Now }, true);
        return doc;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // WAL files can linger briefly on Windows; temp folder cleanup is best effort.
        }
    }
}
