using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;
using F = TravelVault.Core.Documents.TravelDocument.Fields;

namespace TravelVault.Core.Parsing.Tests;

/// <summary>End-to-end parsing of the synthetic tickets in samples/synthetic.</summary>
public class SampleTicketTests
{
    private static readonly DateOnly Imported = new(2026, 11, 17);

    private static ParsedDocument Parse(string sample, params Barcode[] barcodes) =>
        TravelDocumentParser.Parse(new ExtractedContent(File.ReadAllText(Path.Combine("samples", sample)), barcodes), Imported);

    [Fact]
    public void Domestic_flight_eticket()
    {
        var doc = Parse("flight-eticket-domestic.txt");
        Assert.Equal(DocumentType.Flight, doc.Type);
        Assert.Equal("X7KQ2P", doc.Pnr);
        Assert.Equal("NF2611781234", doc.BookingReference);
        Assert.Equal("IndiGo", doc.Provider);

        var leg = Assert.Single(doc.Segments);
        Assert.Equal("BLR", leg.From.Code);
        Assert.Equal("DEL", leg.To!.Code);
        Assert.Equal("Asia/Kolkata", leg.From.TimeZoneId);
        Assert.Equal(new DateTime(2026, 12, 12, 6, 10, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2026, 12, 12, 8, 55, 0), leg.Arrives!.Value.Local);
        Assert.Equal("6E 2345", leg.ServiceNumber);
        Assert.Equal("14C", leg.Seat);
        Assert.Equal(["Mr Rahul Sharma", "Mrs Priya Sharma"], doc.PassengerNames);
        Assert.False(doc.NeedsReview);
    }

    [Fact]
    public void International_connection_has_two_legs_with_their_own_time_zones()
    {
        var doc = Parse("flight-connecting-international.txt");
        Assert.Equal(DocumentType.Flight, doc.Type);
        Assert.Equal("QWE7RT", doc.Pnr);
        Assert.Equal("Emirates", doc.Provider);
        Assert.Equal(2, doc.Segments.Count);
        Assert.Equal(("BLR", "DXB"), (doc.Segments[0].From.Code, doc.Segments[0].To!.Code));
        Assert.Equal(("DXB", "LHR"), (doc.Segments[1].From.Code, doc.Segments[1].To!.Code));
        Assert.Equal(new DateTime(2027, 1, 15, 4, 25, 0), doc.Segments[0].Departs!.Value.Local);
        Assert.Equal("Asia/Dubai", doc.Segments[1].From.TimeZoneId);
        Assert.Equal(new DateTime(2027, 1, 15, 9, 10, 0), doc.Segments[1].Departs!.Value.Local);
        Assert.Equal("EK 7", doc.Segments[1].ServiceNumber);
        Assert.Contains("SHARMA/ANANYA MS", doc.PassengerNames);
    }

    [Fact]
    public void Us_style_dates_and_city_names()
    {
        var doc = Parse("flight-us-style.txt");
        Assert.Equal(DocumentType.Flight, doc.Type);
        Assert.Equal("HGT5KZ", doc.Pnr);
        Assert.Equal("Delta Air Lines", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal("Atlanta", leg.From.City);
        Assert.Equal("Seattle", leg.To!.City);
        Assert.Equal(new DateTime(2027, 3, 5, 8, 15, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2027, 3, 5, 10, 40, 0), leg.Arrives!.Value.Local);
        Assert.Contains("John Michael Carter", doc.PassengerNames);
    }

    [Fact]
    public void Boarding_pass_barcode_is_decoded_exactly_and_time_comes_from_the_text()
    {
        var doc = Parse("boarding-pass.txt", new Barcode(BarcodeFormat.Pdf417, "M1DESMARAIS/LUC       EABC123 YULFRAAC 0834 326J001A0025 100"));
        Assert.Equal(DocumentType.BoardingPass, doc.Type);
        Assert.Equal("ABC123", doc.Pnr);
        Assert.Equal("Air Canada", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal("AC 834", leg.ServiceNumber);
        Assert.Equal("1A", leg.Seat);
        Assert.Equal(new DateTime(2026, 11, 22, 18, 45, 0), leg.Departs!.Value.Local);
        Assert.Equal("America/Toronto", leg.From.TimeZoneId);
        Assert.Equal("DESMARAIS/LUC", doc.PassengerNames[0]);
        Assert.Equal(Confidence.High, doc.Confidence[F.Segments]);
    }

    [Fact]
    public void Boarding_pass_without_a_readable_barcode_still_parses_from_text()
    {
        var doc = Parse("boarding-pass.txt");
        Assert.Equal(DocumentType.BoardingPass, doc.Type);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("YUL", "FRA"), (leg.From.Code, leg.To!.Code));
    }

    [Fact]
    public void Indian_railway_style_ticket()
    {
        var doc = Parse("train-india-style.txt");
        Assert.Equal(DocumentType.Train, doc.Type);
        Assert.Equal("4521378965", doc.Pnr);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal("KSR BENGALURU", leg.From.Name);
        Assert.Equal("SBC", leg.From.Code);
        Assert.Equal("NDLS", leg.To!.Code);
        Assert.Equal("12627", leg.ServiceNumber);
        Assert.Equal(new DateTime(2026, 12, 20, 19, 20, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2026, 12, 22, 8, 45, 0), leg.Arrives!.Value.Local);
        Assert.Equal("B4/32", leg.Seat);
        Assert.Equal(["RAHUL SHARMA", "PRIYA SHARMA"], doc.PassengerNames);
    }

    [Fact]
    public void European_train_with_arrow_route()
    {
        var doc = Parse("train-europe.txt");
        Assert.Equal(DocumentType.Train, doc.Type);
        Assert.Equal("EST4QX9", doc.BookingReference);
        Assert.Equal("Eurostar", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal("London St Pancras", leg.From.Name);
        Assert.Equal("Paris Gare du Nord", leg.To!.Name);
        Assert.Equal(new DateTime(2027, 2, 14, 9, 31, 0), leg.Departs!.Value.Local);
        Assert.Equal(["Emma Thompson", "Oliver Thompson"], doc.PassengerNames);
    }

    [Fact]
    public void Bus_ticket()
    {
        var doc = Parse("bus-ticket.txt");
        Assert.Equal(DocumentType.Bus, doc.Type);
        Assert.Equal("5591XK", doc.Pnr);
        Assert.Equal("TS8K2M4N", doc.BookingReference);
        Assert.Equal("SRS Travels", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal("Majestic, Bengaluru", leg.From.Name);
        Assert.Equal("Koyambedu, Chennai", leg.To!.Name);
        Assert.Equal(new DateOnly(2027, 1, 18), DateOnly.FromDateTime(leg.Departs!.Value.Local));
        Assert.Contains("Mr Vikram Rao", doc.PassengerNames);
    }

    [Fact]
    public void Hotel_booking()
    {
        var doc = Parse("hotel-booking.txt");
        Assert.Equal(DocumentType.Hotel, doc.Type);
        Assert.Equal("The Grand Residency Hotel", doc.Provider);
        var stay = Assert.Single(doc.Segments);
        Assert.Equal(TravelMode.Stay, stay.Mode);
        Assert.Equal("New Delhi", stay.From.City);
        Assert.Equal(new DateOnly(2026, 12, 12), DateOnly.FromDateTime(stay.Departs!.Value.Local));
        Assert.Equal(new DateOnly(2026, 12, 15), DateOnly.FromDateTime(stay.Arrives!.Value.Local));
        Assert.Contains("Rahul Sharma", doc.PassengerNames);
    }

    [Fact]
    public void Event_ticket()
    {
        var doc = Parse("event-ticket.txt");
        Assert.Equal(DocumentType.Event, doc.Type);
        Assert.Equal("Coldplay - Music Of The Spheres", doc.Provider);
        var slot = Assert.Single(doc.Segments);
        Assert.Equal("Wembley Stadium, London", slot.From.Name);
        Assert.Equal(new DateTime(2027, 9, 4, 19, 0, 0), slot.Departs!.Value.Local);
        Assert.Contains("Emma Thompson", doc.PassengerNames);
    }

    [Fact]
    public void Screenshot_without_a_name_has_no_passengers_so_the_uploader_will_be_used()
    {
        var doc = Parse("event-screenshot-no-name.txt");
        Assert.Equal(DocumentType.Event, doc.Type);
        Assert.Equal("BMS7781123", doc.BookingReference);
        Assert.Empty(doc.PassengerNames);
        Assert.Equal(Confidence.Low, doc.Confidence[F.Passengers]);
        Assert.Equal(new DateTime(2026, 12, 18, 21, 45, 0), doc.Segments[0].Departs!.Value.Local);
    }

    [Fact]
    public void Visa_confirmation()
    {
        var doc = Parse("visa-evisa.txt");
        Assert.Equal(DocumentType.Visa, doc.Type);
        Assert.Equal("GWF0482231", doc.Identity!.Number);
        Assert.Equal(new DateOnly(2027, 5, 31), doc.Identity.ExpiresOn);
        Assert.Equal(new DateOnly(2026, 12, 1), doc.Identity.ValidFrom);
        Assert.Equal("United Kingdom", doc.Identity.VisaCountry);
        Assert.Contains("RAHUL SHARMA", doc.PassengerNames);
    }

    [Fact]
    public void Passport_page_is_read_from_the_mrz()
    {
        var doc = Parse("passport-mrz.txt");
        Assert.Equal(DocumentType.Passport, doc.Type);
        Assert.Equal("L898902C3", doc.Identity!.Number);
        Assert.Equal(new DateOnly(2012, 4, 15), doc.Identity.ExpiresOn);
        Assert.Equal("ANNA MARIA ERIKSSON", doc.PassengerNames.Single());
        Assert.Equal(Confidence.High, doc.Confidence[F.Identity]);
    }

    [Fact]
    public void Unrelated_text_is_other_and_needs_review()
    {
        var doc = TravelDocumentParser.Parse(new ExtractedContent("Grocery list: milk, eggs, bread", []), Imported);
        Assert.Equal(DocumentType.Other, doc.Type);
        Assert.True(doc.NeedsReview);
    }
}

public class MediaTypeTests
{
    [Fact]
    public void Detects_types_from_bytes_not_names()
    {
        Assert.Equal(MediaTypes.Pdf, MediaTypes.Detect("%PDF-1.7\n..."u8));
        Assert.Equal(MediaTypes.Jpeg, MediaTypes.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));
        Assert.Equal(MediaTypes.Png, MediaTypes.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal(MediaTypes.Text, MediaTypes.Detect("PNR: X7KQ2P"u8));
        Assert.Null(MediaTypes.Detect([0x00, 0x01, 0x02, 0x03]));
        Assert.Null(MediaTypes.Detect([]));
    }

    [Fact]
    public void Airport_directory_is_bundled()
    {
        var directory = AirportDirectory.Instance;
        Assert.True(directory.Count > 7000);
        Assert.Equal("Asia/Kolkata", directory.Find("BLR")!.TimeZoneId);
        Assert.Contains(directory.FindByCity("Bengaluru"), a => a.Code == "BLR");
        Assert.Contains("Bengaluru", directory.CityNames("Bangalore"));
    }
}
