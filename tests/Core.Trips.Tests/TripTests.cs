using TravelVault.Core.Documents;
using TravelVault.Core.Duplicates;
using TravelVault.Core.Trips;

namespace TravelVault.Core.Trips.Tests;

public class TripGrouperTests
{
    [Fact]
    public void Outbound_hotel_and_return_become_one_suggestion()
    {
        var outbound = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0));
        var hotel = Build.Hotel("New Delhi", new DateTime(2026, 12, 12, 14, 0, 0), new DateTime(2026, 12, 15, 11, 0, 0));
        var back = Build.Flight("DEL", "BLR", new DateTime(2026, 12, 15, 18, 0, 0), pnr: "ABC123");

        var suggestion = Assert.Single(TripGrouper.Suggest([back, hotel, outbound]));

        Assert.Equal([outbound.Id, hotel.Id, back.Id], suggestion.DocumentIds);
        Assert.Equal("New Delhi, 12–15 Dec 2026", suggestion.Name);
    }

    [Fact]
    public void Multi_city_trip_chains_through_cities()
    {
        var a = Build.Flight("BLR", "DEL", new DateTime(2027, 1, 2, 6, 0, 0));
        var b = Build.Flight("DEL", "BOM", new DateTime(2027, 1, 4, 9, 0, 0), pnr: "QQQ111");
        var c = Build.Flight("BOM", "BLR", new DateTime(2027, 1, 7, 9, 0, 0), pnr: "WWW222");
        Assert.Equal(3, Assert.Single(TripGrouper.Suggest([a, b, c])).DocumentIds.Count);
    }

    [Fact]
    public void Long_gap_or_different_people_do_not_chain()
    {
        var a = Build.Flight("BLR", "DEL", new DateTime(2027, 1, 2, 6, 0, 0));
        var later = Build.Flight("DEL", "BLR", new DateTime(2027, 1, 20, 6, 0, 0), pnr: "LATE11");
        var someoneElse = Build.Flight("DEL", "BLR", new DateTime(2027, 1, 3, 6, 0, 0), pnr: "OTHER1", travelers: "priya");
        Assert.Empty(TripGrouper.Suggest([a, later, someoneElse]));
    }

    [Fact]
    public void Documents_already_in_a_trip_are_left_alone()
    {
        var a = Build.Flight("BLR", "DEL", new DateTime(2027, 1, 2, 6, 0, 0));
        var b = Build.Flight("DEL", "BLR", new DateTime(2027, 1, 4, 6, 0, 0), pnr: "ABC123");
        a.TripId = "trip1";
        Assert.Empty(TripGrouper.Suggest([a, b]));
    }

    [Theory]
    [InlineData(-1, TripStatus.Upcoming)]
    [InlineData(24, TripStatus.Ongoing)]
    [InlineData(80, TripStatus.Ongoing)]   // within 6h after the end
    [InlineData(100, TripStatus.Completed)]
    public void Status_follows_the_clock(int hoursAfterStart, TripStatus expected)
    {
        var start = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(76);
        Assert.Equal(expected, TripStatuses.Of(start, end, start.AddHours(hoursAfterStart)));
    }

    [Fact]
    public void Date_ranges_read_naturally()
    {
        Assert.Equal("12–15 Dec 2026", TripStatuses.DateRange(new DateTime(2026, 12, 12), new DateTime(2026, 12, 15)));
        Assert.Equal("28 Dec 2026 – 3 Jan 2027", TripStatuses.DateRange(new DateTime(2026, 12, 28), new DateTime(2027, 1, 3)));
        Assert.Equal("30 Nov – 2 Dec 2026", TripStatuses.DateRange(new DateTime(2026, 11, 30), new DateTime(2026, 12, 2)));
    }
}

public class BoardingPassTests
{
    [Fact]
    public void Links_to_the_matching_segment_and_traveler()
    {
        var ticket = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0), travelers: ["rahul", "priya"]);
        var pass = Build.BoardingPass("BLR", "DEL", new DateTime(2026, 12, 12, 0, 0, 0), "X7KQ2P", "6E 2345", "priya");

        var link = BoardingPassLinker.FindTarget(pass, [ticket]);

        Assert.Equal(new BoardingPassLink(ticket.Id, ticket.Segments[0].Id, "priya"), link);
    }

    [Fact]
    public void Flight_number_alone_is_enough_when_the_pnr_differs()
    {
        var ticket = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0), pnr: "OTA999");
        var pass = Build.BoardingPass("BLR", "DEL", new DateTime(2026, 12, 12, 0, 0, 0), "X7KQ2P", "6E2345", "rahul");
        Assert.NotNull(BoardingPassLinker.FindTarget(pass, [ticket]));
    }

    [Fact]
    public void Different_route_or_date_does_not_link()
    {
        var ticket = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0));
        Assert.Null(BoardingPassLinker.FindTarget(Build.BoardingPass("DEL", "BLR", new DateTime(2026, 12, 12, 0, 0, 0), "X7KQ2P", "6E 2345", "rahul"), [ticket]));
        Assert.Null(BoardingPassLinker.FindTarget(Build.BoardingPass("BLR", "DEL", new DateTime(2026, 12, 20, 0, 0, 0), "X7KQ2P", "6E 2345", "rahul"), [ticket]));
    }

    [Fact]
    public void Missing_boarding_passes_are_per_traveler()
    {
        var ticket = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0), travelers: ["rahul", "priya"]);
        var segment = ticket.Segments[0];
        Assert.Equal(["rahul", "priya"], BoardingPassLinker.MissingFor(ticket, segment, [ticket]));

        var pass = Build.BoardingPass("BLR", "DEL", new DateTime(2026, 12, 12, 0, 0, 0), "X7KQ2P", "6E 2345", "priya");
        pass.BoardingPass = new BoardingPassLink(ticket.Id, segment.Id, "priya");

        Assert.Equal(["rahul"], BoardingPassLinker.MissingFor(ticket, segment, [ticket, pass]));
        Assert.Single(BoardingPassLinker.LinkedTo(ticket, [ticket, pass]));
    }
}

public class DuplicateDetectorTests
{
    [Fact]
    public void Same_pnr_people_and_day_is_a_strong_duplicate()
    {
        var original = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0));
        var updated = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 8, 30, 0), pnr: "x7kq-2p");
        var candidate = Assert.Single(DuplicateDetector.Find(updated, [original]));
        Assert.Equal(DuplicateStrength.Strong, candidate.Strength);
    }

    [Fact]
    public void Same_route_and_day_without_references_is_weak()
    {
        var a = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0), pnr: "");
        var b = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0), pnr: "");
        Assert.Equal(DuplicateStrength.Weak, Assert.Single(DuplicateDetector.Find(a, [b])).Strength);
    }

    [Fact]
    public void Different_people_dates_or_types_are_not_duplicates()
    {
        var a = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0));
        var otherPerson = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 12, 6, 10, 0), travelers: "priya");
        var otherDay = Build.Flight("BLR", "DEL", new DateTime(2026, 12, 19, 6, 10, 0));
        var boardingPass = Build.BoardingPass("BLR", "DEL", new DateTime(2026, 12, 12, 0, 0, 0), "X7KQ2P", "6E 2345", "rahul");
        Assert.Empty(DuplicateDetector.Find(a, [otherPerson, otherDay, boardingPass]));
    }

    [Fact]
    public void Same_passport_number_is_a_duplicate()
    {
        var a = TravelDocument.Create("fam", DocumentType.Passport, "me", Build.Now);
        a.Identity = new IdentityInfo("Z1234567", "IN", null, null);
        var b = TravelDocument.Create("fam", DocumentType.Passport, "me", Build.Now);
        b.Identity = new IdentityInfo("Z1234567", "IN", null, null);
        Assert.Single(DuplicateDetector.Find(a, [b]));
    }
}
