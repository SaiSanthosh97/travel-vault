using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Search;
using TravelVault.Core.Travelers;

namespace TravelVault.Core.Search.Tests;

public class SearchTests
{
    [Fact]
    public void Index_entry_contains_codes_city_aliases_flight_numbers_and_people()
    {
        var rahul = Traveler.Create("fam", "Rahul Sharma");
        rahul.AddAlias("R K SHARMA");
        var doc = TravelDocument.Create("fam", DocumentType.Flight, "me", DateTimeOffset.UtcNow);
        doc.Pnr = "X7KQ2P";
        doc.Segments.Add(new Segment
        {
            Id = Ids.New(),
            From = new Place("Kempegowda", "BLR", "Bangalore", "IN"),
            To = new Place("IGI", "DEL", "New Delhi", "IN"),
            ServiceNumber = "6E 2345",
        });
        doc.SetPassengers([new DocumentPassenger(rahul.Id, "MR RAHUL SHARMA", Confidence.High)], rahul.Id);

        var entry = SearchEntries.For(doc, new Dictionary<string, Traveler> { [rahul.Id] = rahul });

        Assert.Contains("Bengaluru", entry.Places);
        Assert.Contains("BLR", entry.Places);
        Assert.Contains("Delhi", entry.Places);
        Assert.Contains("6E2345", entry.Refs);
        Assert.Contains("X7KQ2P", entry.Refs);
        Assert.Contains("Rahul Sharma", entry.Passengers);
        Assert.Contains("R K SHARMA", entry.Passengers);
    }

    [Theory]
    [InlineData("del", "\"del\"*")]
    [InlineData("  Delhi 6E ", "\"Delhi\"* \"6E\"*")]
    [InlineData("x7kq-2p", "\"x7kq\"* \"2p\"*")]
    [InlineData("\"; DROP TABLE", "\"DROP\"* \"TABLE\"*")]
    [InlineData("  ", null)]
    [InlineData("***", null)]
    public void User_text_becomes_a_safe_prefix_query(string input, string? expected) => Assert.Equal(expected, SearchQuery.ToFts(input));
}
