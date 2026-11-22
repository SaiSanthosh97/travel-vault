using System.Globalization;
using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;

namespace TravelVault.Core.Parsing.Tests;

/// <summary>
/// Flight legs printed as cards (MakeMyTrip e-ticket, round trip with a change of planes each way). The fixture keeps the
/// word positions of a real PDF with a made-up name, PNR, booking ID and e-ticket number (C-029).
/// </summary>
public class FlightCardTests
{
    private static readonly DateOnly Imported = new(2026, 9, 5);

    private static ParsedDocument Parse(DateOnly? imported = null)
    {
        var words = File.ReadAllLines(Path.Combine("samples", "mmt-flight-cards.words.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(p => new LayoutWord(p[4], Num(p[0]), Num(p[1]), Num(p[0]) + Num(p[2]), Num(p[1]) + Num(p[3]), int.Parse(p[5], CultureInfo.InvariantCulture)))
            .ToList();
        var layout = TextLayout.FromWords(words);
        return TravelDocumentParser.Parse(new ExtractedContent(layout.Text, []) { Layout = layout }, imported ?? Imported);
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string? Detail(ParsedDocument doc, string label) => doc.Details.FirstOrDefault(d => d.Label == label)?.Value;

    [Fact]
    public void Every_leg_card_becomes_a_segment_with_its_own_times_and_flight()
    {
        var doc = Parse();

        Assert.Equal(DocumentType.Flight, doc.Type);
        Assert.Equal("Etihad Airways", doc.Provider);
        Assert.Equal("X7KQ2P", doc.Pnr);
        Assert.Collection(doc.Segments,
            s => Leg(s, "EY 233", "BLR", "AUH", new(2026, 10, 7, 4, 35, 0), new(2026, 10, 7, 7, 0, 0)),
            s => Leg(s, "EY 65", "AUH", "LHR", new(2026, 10, 7, 10, 35, 0), new(2026, 10, 7, 15, 5, 0)),
            s => Leg(s, "EY 64", "LHR", "AUH", new(2026, 10, 22, 14, 45, 0), new(2026, 10, 23, 0, 45, 0)), // the arrival side sits a row higher
            s => Leg(s, "EY 1012", "AUH", "BLR", new(2026, 10, 23, 3, 0, 0), new(2026, 10, 23, 8, 45, 0)));
        Assert.Equal("Asia/Dubai", doc.Segments[0].To!.TimeZoneId);
        Assert.False(doc.NeedsReview);

        static void Leg(Segment s, string flight, string from, string to, DateTime departs, DateTime arrives)
        {
            Assert.Equal(flight, s.ServiceNumber);
            Assert.Equal((from, to), (s.From.Code, s.To!.Code));
            Assert.Equal(departs, s.Departs!.Value.Local);
            Assert.Equal(arrives, s.Arrives!.Value.Local);
        }
    }

    [Fact]
    public void Long_travel_site_booking_id_and_a_single_passenger_from_a_wrapped_table_cell()
    {
        var doc = Parse();

        Assert.Equal("NN0TEST000000000001", doc.BookingReference); // not the e-ticket number
        Assert.Equal("2400000001", Detail(doc, "E-ticket"));
        Assert.Equal(["Mr RIA KUMAR DEVI SHARMA"], doc.PassengerNames); // fine print ("travellers must present…") is not a name
        Assert.Equal("5 Sep 2026", Detail(doc, "Booked on"));
    }

    [Fact]
    public void Dates_without_a_year_take_it_from_the_printed_weekday()
    {
        // Imported long after the trip: "Wed, Oct 7" would otherwise become the next 7 October (2027, a Thursday).
        var doc = Parse(new DateOnly(2027, 3, 1));
        Assert.Equal(new DateTime(2026, 10, 7, 4, 35, 0), doc.Segments[0].Departs!.Value.Local);

        var facts = TextFacts.Analyze("Fri, Oct 23", new DateOnly(2028, 1, 10));
        Assert.Equal(new DateOnly(2026, 10, 23), Assert.Single(facts.DateTimes).Date);
    }
}
