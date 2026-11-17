using System.Text;
using TravelVault.Core.Documents;
using TravelVault.Core.Duplicates;
using TravelVault.Core.Parsing;
using TravelVault.Core.Trips;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Vault.Tests;

public class ImportUseCaseTests
{
    private const string Family = TestVault.FamilyId;

    [Fact]
    public async Task Import_creates_travelers_stores_the_file_encrypted_and_makes_it_searchable()
    {
        using var vault = new TestVault();
        var result = await vault.Import.PrepareAsync(Family, TestVault.Sample("flight-eticket-domestic.txt"), "ticket.txt");

        Assert.Equal(ImportStatus.Ready, result.Status);
        var draft = result.Draft!;
        Assert.Equal(DocumentType.Flight, draft.Document.Type);
        Assert.Equal(2, draft.Names.Count);

        var saved = await vault.Import.SaveAsync(draft);

        var travelers = await vault.Travelers.ListAsync(Family);
        Assert.Contains(travelers, t => t.DisplayName == "Priya Sharma");
        Assert.Contains(travelers, t => t.DisplayName == "Rahul Sharma");
        Assert.Equal(2, saved.Passengers.Count);
        Assert.Equal("Flight · Bangalore → New Delhi", saved.Title);

        var opened = await vault.DocumentService.OpenFileAsync(saved.Id);
        Assert.Equal(FileAvailability.Present, opened.Availability);
        Assert.Equal(TestVault.Sample("flight-eticket-domestic.txt"), opened.Content);

        Assert.Single(await vault.Search.SearchAsync(Family, "bengaluru"));
        Assert.Single(await vault.Search.SearchAsync(Family, "x7kq2p"));
        Assert.Single(await vault.Search.SearchAsync(Family, "priya"));
        Assert.Single(await vault.Search.SearchAsync(Family, "6E2345"));
        Assert.Empty(await vault.Search.SearchAsync(Family, "chennai"));
    }

    [Fact]
    public async Task The_same_file_twice_is_recognised_as_already_in_the_vault()
    {
        using var vault = new TestVault();
        var bytes = TestVault.Sample("hotel-booking.txt");
        await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, bytes, null)).Draft!);

        var second = await vault.Import.PrepareAsync(Family, bytes, null);

        Assert.Equal(ImportStatus.AlreadyInVault, second.Status);
        Assert.NotNull(second.Existing);
    }

    [Fact]
    public async Task An_updated_eticket_is_offered_as_a_merge_and_becomes_a_new_version()
    {
        using var vault = new TestVault();
        var original = await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, TestVault.Sample("flight-eticket-domestic.txt"), null)).Draft!);

        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestVault.Sample("flight-eticket-domestic.txt")).Replace("06:10", "07:40", StringComparison.Ordinal));
        vault.Clock.Advance(TimeSpan.FromDays(2));
        var draft = (await vault.Import.PrepareAsync(Family, changed, null)).Draft!;

        var duplicate = Assert.Single(draft.Duplicates);
        Assert.Equal(DuplicateStrength.Strong, duplicate.Strength);
        Assert.False(draft.CanSaveWithoutReview);

        var merged = await vault.Import.SaveAsync(draft, mergeInto: duplicate.Existing.Id);

        Assert.Equal(original.Id, merged.Id);
        Assert.Equal(2, merged.Files.Count);
        Assert.Equal(7, merged.Segments[0].Departs!.Value.Local.Hour);
        Assert.Single(await vault.DocumentService.ListAsync(Family));
    }

    [Fact]
    public async Task A_ticket_without_a_name_belongs_to_the_uploader()
    {
        using var vault = new TestVault();
        var draft = (await vault.Import.PrepareAsync(Family, TestVault.Sample("event-screenshot-no-name.txt"), null)).Draft!;
        var saved = await vault.Import.SaveAsync(draft);

        var me = await vault.Travelers.MyTravelerAsync(Family);
        Assert.Equal(me.Id, Assert.Single(saved.Passengers).TravelerId);
        Assert.Equal("member-me", me.LinkedMemberId);
    }

    [Fact]
    public async Task A_boarding_pass_photo_links_to_the_ticket_and_clears_the_missing_pass()
    {
        using var vault = new TestVault(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var ticketText = "Air Canada e-ticket\nBooking reference: ABC123\nFlight AC 834\nMontreal (YUL) to Frankfurt (FRA)\nDeparture: 22 Nov 2026 18:45\nPassenger: Luc Desmarais";
        var ticket = await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, Encoding.UTF8.GetBytes(ticketText), null)).Draft!);
        Assert.Equal(DocumentType.Flight, ticket.Type);
        Assert.Single(BoardingPassLinker.MissingFor(ticket, ticket.Segments[0], [ticket]));

        var photo = vault.Photo("bp1", "BOARDING PASS\nDeparture 18:45", new Barcode(BarcodeFormat.Pdf417, "M1DESMARAIS/LUC       EABC123 YULFRAAC 0834 326J001A0025 100"));
        var draft = (await vault.Import.PrepareAsync(Family, photo, "bp.png")).Draft!;
        Assert.True(draft.Names.Single().IsConfident); // "DESMARAIS/LUC" matches the traveler created from the ticket
        var pass = await vault.Import.SaveAsync(draft);

        Assert.Equal(DocumentType.BoardingPass, pass.Type);
        Assert.Equal(ticket.Id, pass.BoardingPass!.TicketDocumentId);
        var all = await vault.DocumentService.ListAsync(Family);
        Assert.Empty(BoardingPassLinker.MissingFor(ticket, ticket.Segments[0], all));
        Assert.Single(await vault.DocumentService.BoardingPassesForAsync(ticket));
    }

    [Fact]
    public async Task Unsupported_files_are_refused()
    {
        using var vault = new TestVault();
        Assert.Equal(ImportStatus.Unsupported, (await vault.Import.PrepareAsync(Family, [0, 1, 2, 3, 0], null)).Status);
    }
}

public class DocumentUseCaseTests
{
    private const string Family = TestVault.FamilyId;

    private static async Task<TravelDocument> ImportAsync(TestVault vault, string sample) =>
        await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, TestVault.Sample(sample), null)).Draft!);

    [Fact]
    public async Task A_missing_file_is_requested_from_family_phones()
    {
        using var vault = new TestVault();
        var doc = await ImportAsync(vault, "bus-ticket.txt");
        await vault.FileKeys.DeleteAsync(doc.PrimaryFile!.Id); // as if it never arrived on this phone

        var opened = await vault.DocumentService.OpenFileAsync(doc.Id);

        Assert.Equal(FileAvailability.Requested, opened.Availability);
        Assert.Equal([doc.PrimaryFile.Id], vault.Requester.Requested);
    }

    [Fact]
    public async Task Edits_are_protected_and_deleted_documents_can_be_restored()
    {
        using var vault = new TestVault();
        var doc = await ImportAsync(vault, "train-europe.txt");
        doc.Title = "Paris weekend";
        await vault.DocumentService.UpdateAsync(doc, [TravelDocument.Fields.Title]);
        Assert.Equal("Paris weekend", (await vault.DocumentService.GetAsync(doc.Id))!.Title);

        await vault.DocumentService.DeleteAsync(doc.Id);
        Assert.Empty(await vault.DocumentService.ListAsync(Family));
        Assert.Empty(await vault.Search.SearchAsync(Family, "paris"));
        Assert.Single(await vault.DocumentService.RecentlyDeletedAsync(Family));

        await vault.DocumentService.RestoreAsync(doc.Id);
        Assert.Single(await vault.Search.SearchAsync(Family, "paris"));
    }

    [Fact]
    public async Task Reassigning_travelers_and_privacy_rules()
    {
        using var vault = new TestVault();
        var doc = await ImportAsync(vault, "event-ticket.txt");
        var ananya = await vault.Travelers.CreateAsync(Family, "Ananya Sharma");

        await vault.DocumentService.ReassignTravelersAsync(doc.Id, [ananya.Id]);
        var updated = (await vault.DocumentService.GetAsync(doc.Id))!;
        Assert.Equal(ananya.Id, Assert.Single(updated.Passengers).TravelerId);
        Assert.Single(await vault.Search.SearchAsync(Family, "ananya"));

        await vault.DocumentService.SetVisibilityAsync(doc.Id, Visibility.Private);
        Assert.Equal(Visibility.Private, (await vault.DocumentService.GetAsync(doc.Id))!.Visibility);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.DocumentService.ReassignTravelersAsync(doc.Id, []));
    }

    [Fact]
    public async Task Next_up_is_the_earliest_future_segment()
    {
        using var vault = new TestVault();
        await ImportAsync(vault, "flight-connecting-international.txt"); // Jan 2027
        await ImportAsync(vault, "flight-eticket-domestic.txt");         // Dec 2026
        var next = await vault.DocumentService.NextUpAsync(Family);
        Assert.Equal("BLR", next!.Value.Segment.From.Code);
        Assert.Equal(12, next.Value.Segment.Departs!.Value.Local.Month);
    }

    [Fact]
    public async Task Renaming_a_traveler_keeps_the_old_name_as_an_alias_and_updates_search()
    {
        using var vault = new TestVault();
        var doc = await ImportAsync(vault, "hotel-booking.txt");
        var traveler = (await vault.Travelers.ListAsync(Family)).Single(t => t.Id == doc.Passengers[0].TravelerId);

        await vault.Travelers.RenameAsync(traveler.Id, "Appa");

        Assert.Single(await vault.Search.SearchAsync(Family, "appa"));
        var renamed = (await vault.Travelers.ListAsync(Family)).Single(t => t.Id == traveler.Id);
        Assert.Contains("Rahul Sharma", renamed.Aliases);
    }
}

public class TripUseCaseTests
{
    private const string Family = TestVault.FamilyId;

    [Fact]
    public async Task Suggested_trip_can_be_confirmed_dismissed_and_listed_by_status()
    {
        using var vault = new TestVault();
        var outbound = await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, TestVault.Sample("flight-eticket-domestic.txt"), null)).Draft!);
        var hotelText = Encoding.UTF8.GetString(TestVault.Sample("hotel-booking.txt"));
        await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, Encoding.UTF8.GetBytes(hotelText), null)).Draft!);
        var back = "IndiGo e-ticket\nPNR: RTN456\n6E 2346   Delhi (DEL) → Bengaluru (BLR)\nDeparture: 15 Dec 2026 18:00\nPassenger: Mr Rahul Sharma";
        await vault.Import.SaveAsync((await vault.Import.PrepareAsync(Family, Encoding.UTF8.GetBytes(back), null)).Draft!);

        var suggestion = Assert.Single(await vault.Trips.SuggestionsAsync(Family));
        Assert.Equal(3, suggestion.DocumentIds.Count);
        Assert.StartsWith("New Delhi", suggestion.Name);

        var trip = await vault.Trips.ConfirmAsync(Family, suggestion, "Delhi wedding");
        Assert.Empty(await vault.Trips.SuggestionsAsync(Family));
        Assert.Equal(trip.Id, (await vault.DocumentService.GetAsync(outbound.Id))!.TripId);

        var byStatus = await vault.Trips.ByStatusAsync(Family);
        Assert.Equal("Delhi wedding", Assert.Single(byStatus[TripStatus.Upcoming]).Trip.Name);

        vault.Clock.Set(new DateTimeOffset(2026, 12, 13, 9, 0, 0, TimeSpan.Zero));
        Assert.Single((await vault.Trips.ByStatusAsync(Family))[TripStatus.Ongoing]);

        await vault.Trips.DeleteAsync(trip.Id);
        Assert.Null((await vault.DocumentService.GetAsync(outbound.Id))!.TripId);
        var again = Assert.Single(await vault.Trips.SuggestionsAsync(Family));
        await vault.Trips.DismissAsync(Family, again);
        Assert.Empty(await vault.Trips.SuggestionsAsync(Family));
    }
}
