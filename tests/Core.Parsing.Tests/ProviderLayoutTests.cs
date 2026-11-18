using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace TravelVault.Core.Parsing.Tests;

/// <summary>
/// Layouts of real Indian and travel-site tickets (IRCTC/RailOne slips, IndiGo, MakeMyTrip, redBus, Booking.com),
/// rebuilt with made-up values. Each one failed or was only partly read before the form reader (C-022).
/// </summary>
public class ProviderLayoutTests
{
    private static readonly DateOnly Imported = new(2026, 11, 17);

    private static ParsedDocument Parse(string sample) =>
        TravelDocumentParser.Parse(new ExtractedContent(File.ReadAllText(Path.Combine("samples", sample)), []), Imported);

    private static string? Detail(ParsedDocument doc, string label) => doc.Details.FirstOrDefault(d => d.Label == label)?.Value;

    [Fact]
    public void Irctc_2023_slip_reads_values_under_their_column_headers()
    {
        var doc = Parse("irctc-ers-2023.txt");
        Assert.Equal(DocumentType.Train, doc.Type);
        Assert.Equal("4521378965", doc.Pnr);
        Assert.Equal("IRCTC", doc.Provider);

        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("AII", "PURI"), (leg.From.Code, leg.To!.Code));
        Assert.Equal("Puri", leg.To.City); // a station named like its code is still a name
        Assert.Equal("AJMER JN", leg.From.Name);
        Assert.Equal("Ajmer", leg.From.City);
        Assert.Equal("Asia/Kolkata", leg.From.TimeZoneId);
        Assert.Equal(new DateTime(2027, 8, 31, 19, 0, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2027, 9, 2, 14, 25, 0), leg.Arrives!.Value.Local);
        Assert.Equal("20824", leg.ServiceNumber);
        Assert.Equal("AII PURI SF EXP", leg.ServiceName);
        Assert.Equal("SLEEPER CLASS (SL)", leg.TravelClass);
        Assert.Equal("S4/56", leg.Seat);

        Assert.Equal(["ANITA VERMA", "ROHIT VERMA"], doc.PassengerNames);
        Assert.Equal("Confirmed", Detail(doc, "Status"));
        Assert.Equal("Confirmed · Coach S4 · Berth 56 · Side upper", Detail(doc, "ANITA VERMA"));
        Assert.Equal("Confirmed · Coach S4 · Berth 53 · Upper", Detail(doc, "ROHIT VERMA"));
        Assert.Equal("GENERAL (GN)", Detail(doc, "Quota"));
        Assert.Equal("2476 KM", Detail(doc, "Distance"));
        Assert.False(doc.NeedsReview);
    }

    [Fact]
    public void Irctc_classic_slip_with_several_label_value_pairs_per_line()
    {
        var doc = Parse("irctc-ers-classic.txt");
        Assert.Equal(DocumentType.Train, doc.Type);
        Assert.Equal("2647193058", doc.Pnr);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("NDLS", "PNBE"), (leg.From.Code, leg.To!.Code));
        Assert.Equal("New Delhi", leg.From.City);
        Assert.Equal("Patna", leg.To.City);
        Assert.Equal(new DateTime(2027, 4, 3, 18, 0, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2027, 4, 4, 13, 0, 0), leg.Arrives!.Value.Local);
        Assert.Equal("14004", leg.ServiceNumber);
        Assert.Equal("MLDT EXP", leg.ServiceName);
        Assert.Equal("THIRD AC (3A)", leg.TravelClass);
        Assert.Equal("B2/37", leg.Seat);
        Assert.Equal(["SUNIL KUMAR"], doc.PassengerNames);
        Assert.Equal("TATKAL (TQ)", Detail(doc, "Quota"));
        Assert.Equal("1022KM", Detail(doc, "Distance"));
        Assert.Equal("100003456789012", Detail(doc, "Transaction ID"));
    }

    [Fact]
    public void Wrapped_values_are_joined_and_a_waitlist_is_reported()
    {
        var doc = Parse("irctc-ers-wrapped.txt");
        Assert.Equal("8812345670", doc.Pnr);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("CAN", "YPR"), (leg.From.Code, leg.To!.Code));
        Assert.Equal("YESVANTPUR JN", leg.To.Name);
        Assert.Equal(new DateTime(2027, 8, 14, 18, 5, 0), leg.Departs!.Value.Local);
        Assert.Equal("16528", leg.ServiceNumber);
        Assert.Equal("YESVANTPUR EXP", leg.ServiceName);
        Assert.Null(leg.Seat);
        Assert.Equal("Waitlist 9", Detail(doc, "Status"));
        Assert.Equal(["LATA NAIR"], doc.PassengerNames);
    }

    [Fact]
    public void IndiGo_boarding_pass_text_without_a_barcode()
    {
        var doc = Parse("indigo-boarding-pass-text.txt");
        Assert.Equal(DocumentType.BoardingPass, doc.Type);
        Assert.Equal("KQ7Z2M", doc.Pnr);
        Assert.Equal("IndiGo", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("DIB", "CCU"), (leg.From.Code, leg.To!.Code));
        Assert.Equal(new DateTime(2027, 4, 8, 14, 40, 0), leg.Departs!.Value.Local); // departure, not the 13:55 boarding time
        Assert.Equal("6E 206", leg.ServiceNumber);
        Assert.Equal("21F", leg.Seat);
        Assert.Equal("3", Detail(doc, "Gate"));
        Assert.Equal("13:55", Detail(doc, "Boarding time"));
        Assert.Contains("Mr Arjun Mehta", doc.PassengerNames);
    }

    [Fact]
    public void IndiGo_itinerary_table_with_city_names_and_12_hour_times()
    {
        var doc = Parse("indigo-itinerary.txt");
        Assert.Equal(DocumentType.Flight, doc.Type);
        Assert.Equal("ZX4P9Q", doc.Pnr);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("BLR", "DEL"), (leg.From.Code, leg.To!.Code));
        Assert.Equal(new DateTime(2027, 11, 16, 21, 10, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2027, 11, 16, 23, 55, 0), leg.Arrives!.Value.Local);
        Assert.Equal("6E 182", leg.ServiceNumber);
        Assert.Equal("Confirmed", Detail(doc, "Status"));
        Assert.Contains("Mrs Kavya Iyer", doc.PassengerNames);
    }

    [Fact]
    public void MakeMyTrip_ticket_takes_the_airline_pnr_from_the_passenger_table()
    {
        var doc = Parse("makemytrip-flight.txt");
        Assert.Equal(DocumentType.Flight, doc.Type);
        Assert.Equal("T4KX9B", doc.Pnr);
        Assert.Equal("NF7R2K81234567", doc.BookingReference);
        Assert.Equal("IndiGo", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("BOM", "DEL"), (leg.From.Code, leg.To!.Code));
        Assert.Equal(new DateTime(2026, 12, 12, 22, 10, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2026, 12, 13, 0, 20, 0), leg.Arrives!.Value.Local);
        Assert.Equal("1", Detail(doc, "Terminal"));
        Assert.Equal("3122456789012", Detail(doc, "E-ticket"));
        Assert.Contains("Mr Karan Malhotra", doc.PassengerNames);
    }

    [Fact]
    public void RedBus_ticket_with_headers_over_values()
    {
        var doc = Parse("redbus-ticket.txt");
        Assert.Equal(DocumentType.Bus, doc.Type);
        Assert.Equal("8812AB34", doc.Pnr);
        Assert.Equal("TT5B8293KQ", doc.BookingReference);
        Assert.Equal("Orange Tours and Travels", doc.Provider);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal("Bengaluru", leg.From.Name);
        Assert.Equal("Hyderabad", leg.To!.Name);
        Assert.Equal(new DateTime(2027, 1, 15, 21, 15, 0), leg.Departs!.Value.Local);
        Assert.Equal("U4, U5", leg.Seat);
        Assert.Equal("A/C Sleeper (2+1)", leg.TravelClass);
        Assert.Equal("Kalasipalyam", Detail(doc, "Boarding point"));
        Assert.Equal("20:45", Detail(doc, "Reporting time"));
        Assert.Contains("Ms Neha Kulkarni", doc.PassengerNames);
    }

    [Fact]
    public void Booking_com_check_in_columns_with_times_on_the_next_row()
    {
        var doc = Parse("bookingcom-hotel-columns.txt");
        Assert.Equal(DocumentType.Hotel, doc.Type);
        Assert.Equal("Sea Breeze Resort", doc.Provider);
        Assert.Equal("4128553912", doc.BookingReference);
        var stay = Assert.Single(doc.Segments);
        Assert.Equal(new DateTime(2027, 3, 12, 14, 0, 0), stay.Departs!.Value.Local);
        Assert.Equal(new DateTime(2027, 3, 15, 11, 0, 0), stay.Arrives!.Value.Local);
        Assert.Equal("7720", Detail(doc, "PIN"));
        Assert.Equal("3", Detail(doc, "Nights"));
        Assert.Equal("Booking.com", Detail(doc, "Booked via"));
        Assert.Contains("Meera Nair", doc.PassengerNames);
    }

    [Theory]
    [InlineData("CNF/S4/56/SU", "S4/56", "Confirmed · Coach S4 · Berth 56 · Side upper")]
    [InlineData("CNF/B2/37/LOWER", "B2/37", "Confirmed · Coach B2 · Berth 37 · Lower")]
    [InlineData("CNF/D3/42/WS", "D3/42", "Confirmed · Coach D3 · Seat 42 · Window")]
    [InlineData("RAC/S2/7", "S2/7", "RAC · Coach S2 · Berth 7")]
    [InlineData("GNWL/23", null, "Waitlist 23")]
    [InlineData("CAN", null, "Cancelled")]
    public void Rail_booking_status_codes(string status, string? seat, string text)
    {
        Assert.Equal((seat, text), TravelDocumentParser.DescribeRailStatus(status));
    }

    /// <summary>
    /// The real path for a PDF: words with positions from the PDF text layer (as on the 2023 slip, where the
    /// columns are ~170 points apart), not plain text. Built with PdfPig's writer so no real ticket is needed.
    /// </summary>
    [Fact]
    public async Task Pdf_slip_is_read_from_word_positions()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 780.0;
        void Row(params (double X, string Text)[] cells)
        {
            foreach (var (x, text) in cells)
            {
                page.AddText(text, 9, new PdfPoint(x, y), font);
            }

            y -= 14;
        }

        Row((40, "IRCTC Electronic Reservation Slip (ERS)"));
        Row((109, "Booked From"), (280, "Boarding At"), (467, "To"));
        Row((106, "KSR BENGALURU (SBC)"), (273, "KSR BENGALURU (SBC)"), (447, "MYSURU JN (MYS)"));
        Row((60, "Start Date* 05-Jan-2027"), (250, "Departure* 06:10 05-Jan-2027"), (430, "Arrival* 08:20 05-Jan-2027"));
        Row((131, "PNR:"), (296, "Train No./Name:"), (456, "Class:"));
        Row((117, "4417392051"), (257, "12007/MYS SHATABDI"), (429, "AC CHAIR CAR (CC)"));
        Row((40, "Passenger Details:"));
        Row((40, "#"), (60, "Name"), (200, "Age"), (240, "Gender"), (300, "Booking Status"), (420, "Current Status"));
        Row((40, "1."), (60, "DEEPA RAO"), (200, "29"), (240, "F"), (300, "CNF/C2/41/WS"), (420, "CNF/C2/41/WS"));

        var pdf = builder.Build();
        var extractor = new ContentExtractor(new NoText(), new NoBarcodes(), new NoPages());
        var content = await extractor.ExtractAsync(pdf, MediaTypes.Pdf, "ticket.pdf");
        Assert.NotNull(content.Layout);

        var doc = TravelDocumentParser.Parse(content, Imported);
        Assert.Equal(DocumentType.Train, doc.Type);
        Assert.Equal("4417392051", doc.Pnr);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("SBC", "MYS"), (leg.From.Code, leg.To!.Code));
        Assert.Equal(new DateTime(2027, 1, 5, 6, 10, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2027, 1, 5, 8, 20, 0), leg.Arrives!.Value.Local);
        Assert.Equal("12007", leg.ServiceNumber);
        Assert.Equal("MYS SHATABDI", leg.ServiceName);
        Assert.Equal("C2/41", leg.Seat);
        Assert.Equal(["DEEPA RAO"], doc.PassengerNames);
    }

    private sealed class NoText : ITextRecognizer
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
