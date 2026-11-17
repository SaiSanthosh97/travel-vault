using TravelVault.Core.Common;
using TravelVault.Core.Documents;

namespace TravelVault.Core.Documents.Tests;

public class TravelDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    internal static FileVersion File(string sha, DateTimeOffset added) =>
        new() { Id = Ids.New(), Sha256 = sha, Size = 100, MediaType = "application/pdf", AddedAt = added };

    internal static Segment Flight(string from, string to, DateTime departs, DateTime? arrives = null, string zone = "Asia/Kolkata") => new()
    {
        Id = Ids.New(),
        Mode = TravelMode.Flight,
        From = new Place(from, from[..3].ToUpperInvariant(), from, "IN", zone),
        To = new Place(to, to[..3].ToUpperInvariant(), to, "IN", zone),
        Departs = new LocalDateTimeZ(departs, zone),
        Arrives = arrives is { } a ? new LocalDateTimeZ(a, zone) : null,
    };

    [Fact]
    public void No_printed_name_means_the_uploader_travels()
    {
        var doc = TravelDocument.Create("fam", DocumentType.Event, "mom-device", Now);
        doc.SetPassengers([], uploaderTravelerId: "mom-traveler");
        Assert.Equal("mom-traveler", Assert.Single(doc.Passengers).TravelerId);
        Assert.Equal(Confidence.Low, doc.Passengers[0].Match);
    }

    [Fact]
    public void First_file_is_primary_and_same_content_is_not_added_twice()
    {
        var doc = TravelDocument.Create("fam", DocumentType.Flight, "m", Now);
        Assert.True(doc.AddFile(File("aa", Now), makePrimary: false));
        Assert.True(doc.Files[0].IsPrimary);
        Assert.False(doc.AddFile(File("aa", Now.AddDays(1)), makePrimary: true));
        Assert.True(doc.AddFile(File("bb", Now.AddDays(1)), makePrimary: true));
        Assert.Equal("bb", doc.PrimaryFile!.Sha256);
        Assert.Single(doc.Files, f => f.IsPrimary);
    }

    [Fact]
    public void Validate_finds_arrival_before_departure_and_missing_traveler()
    {
        var doc = TravelDocument.Create("fam", DocumentType.Flight, "m", Now);
        doc.Segments.Add(Flight("Bengaluru", "Delhi", new DateTime(2026, 12, 12, 9, 0, 0), new DateTime(2026, 12, 12, 6, 0, 0)));
        var problems = doc.Validate();
        Assert.Contains(problems, p => p.Contains("Arrival"));
        Assert.Contains(problems, p => p.Contains("traveler"));
    }

    [Fact]
    public void Times_are_kept_as_printed_and_converted_with_the_place_zone()
    {
        // 06:10 in India = 00:40 UTC
        var segment = Flight("Bengaluru", "Delhi", new DateTime(2026, 12, 12, 6, 10, 0));
        Assert.Equal(new DateTimeOffset(2026, 12, 12, 0, 40, 0, TimeSpan.Zero), segment.DepartsUtc);
        Assert.Equal(6, segment.Departs!.Value.Local.Hour);
    }

    [Fact]
    public void Unknown_zone_falls_back_without_crashing()
    {
        var t = new LocalDateTimeZ(new DateTime(2026, 1, 1, 10, 0, 0), "Not/AZone");
        Assert.Equal(TimeSpan.Zero, t.ToUtc(TimeZoneInfo.Utc).Offset);
        Assert.Equal(10, t.ToUtc(TimeZoneInfo.Utc).Hour);
    }

    [Fact]
    public void Titles_describe_the_route_and_respect_user_edits()
    {
        var doc = TravelDocument.Create("fam", DocumentType.Flight, "m", Now);
        doc.Segments.Add(Flight("Bengaluru", "Delhi", new DateTime(2026, 12, 12, 6, 10, 0)));
        DocumentTitles.Refresh(doc);
        Assert.Equal("Flight · Bengaluru → Delhi", doc.Title);

        doc.Title = "Mom's Delhi trip";
        doc.MarkEdited(TravelDocument.Fields.Title);
        DocumentTitles.Refresh(doc);
        Assert.Equal("Mom's Delhi trip", doc.Title);
    }

    [Theory]
    [InlineData("x7kq-2p", "X7KQ2P")]
    [InlineData(" X7KQ2P ", "X7KQ2P")]
    [InlineData("ab", null)]
    [InlineData(null, null)]
    public void Booking_codes_normalize(string? raw, string? expected) => Assert.Equal(expected, BookingCodes.Normalize(raw));

    [Fact]
    public void Identity_numbers_are_masked()
    {
        Assert.Equal("•••••4567", new IdentityInfo("Z12344567", "IN", null, null).MaskedNumber);
        Assert.Equal("123", new IdentityInfo("123", null, null, null).MaskedNumber);
    }
}

public class MergeAndConflictTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static TravelDocument Ticket(string sha, DateTimeOffset added, DateTime departs)
    {
        var doc = TravelDocument.Create("fam", DocumentType.Flight, "m", added);
        doc.Pnr = "X7KQ2P";
        doc.Segments.Add(TravelDocumentTests.Flight("Bengaluru", "Delhi", departs));
        doc.SetPassengers([new DocumentPassenger("rahul", "RAHUL SHARMA", Confidence.High)], "rahul");
        doc.AddFile(TravelDocumentTests.File(sha, added), makePrimary: true);
        return doc;
    }

    [Fact]
    public void Updated_eticket_becomes_primary_version_and_brings_new_times()
    {
        var original = Ticket("old", Now, new DateTime(2026, 12, 12, 6, 10, 0));
        var updated = Ticket("new", Now.AddDays(2), new DateTime(2026, 12, 12, 8, 30, 0));

        DocumentMerger.Merge(original, updated, Now.AddDays(2));

        Assert.Equal(2, original.Files.Count);
        Assert.Equal("new", original.PrimaryFile!.Sha256);
        Assert.Equal(8, original.Segments[0].Departs!.Value.Local.Hour);
        Assert.True(updated.IsDeleted);
        Assert.Equal(original.Id, updated.MergedInto);
    }

    [Fact]
    public void Merge_keeps_user_edits_on_the_target()
    {
        var original = Ticket("old", Now, new DateTime(2026, 12, 12, 6, 10, 0));
        original.MarkEdited(TravelDocument.Fields.Segments);
        var updated = Ticket("new", Now.AddDays(2), new DateTime(2026, 12, 12, 8, 30, 0));

        DocumentMerger.Merge(original, updated, Now);

        Assert.Equal(6, original.Segments[0].Departs!.Value.Local.Hour);
    }

    [Fact]
    public void Merge_combines_passengers_and_fills_empty_fields()
    {
        var a = Ticket("a", Now, new DateTime(2026, 12, 12, 6, 0, 0));
        a.Provider = null;
        var b = Ticket("b", Now.AddMinutes(-5), new DateTime(2026, 12, 12, 6, 0, 0));
        b.Provider = "IndiGo";
        b.SetPassengers([new DocumentPassenger("priya", "PRIYA SHARMA", Confidence.High)], "x");

        DocumentMerger.Merge(a, b, Now);

        Assert.Equal("IndiGo", a.Provider);
        Assert.Equal(2, a.Passengers.Count);
        Assert.Equal("a", a.PrimaryFile!.Sha256); // b's file is older, so a's stays primary
    }

    [Fact]
    public void Cannot_merge_a_document_with_itself()
    {
        var a = Ticket("a", Now, new DateTime(2026, 12, 12, 6, 0, 0));
        Assert.Throws<InvalidOperationException>(() => DocumentMerger.Merge(a, a, Now));
    }

    [Fact]
    public void Newer_incoming_version_wins_but_local_user_edit_survives()
    {
        var local = Ticket("a", Now, new DateTime(2026, 12, 12, 6, 0, 0));
        local.Version = new HybridTimestamp(1, 0, "phoneA");
        local.Pnr = "ABC123";
        local.MarkEdited(TravelDocument.Fields.Pnr);

        var incoming = Ticket("a", Now, new DateTime(2026, 12, 12, 7, 0, 0));
        incoming.Version = new HybridTimestamp(2, 0, "phoneB");
        incoming.Pnr = "X7KQ2P";

        var result = DocumentConflicts.Resolve(local, incoming);

        Assert.Same(incoming, result);
        Assert.Equal("ABC123", result.Pnr);
        Assert.Equal(7, result.Segments[0].Departs!.Value.Local.Hour);
    }

    [Fact]
    public void Older_incoming_version_is_ignored()
    {
        var local = Ticket("a", Now, new DateTime(2026, 12, 12, 6, 0, 0));
        local.Version = new HybridTimestamp(5, 0, "a");
        var incoming = Ticket("a", Now, new DateTime(2026, 12, 12, 7, 0, 0));
        incoming.Version = new HybridTimestamp(4, 0, "b");
        Assert.Same(local, DocumentConflicts.Resolve(local, incoming));
    }
}
