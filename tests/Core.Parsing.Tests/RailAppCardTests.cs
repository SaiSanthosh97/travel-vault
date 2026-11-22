using System.Globalization;
using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;

namespace TravelVault.Core.Parsing.Tests;

/// <summary>
/// A RailOne app ticket screen (shared as a screenshot): nothing is labelled, the meaning is in the placement.
/// The fixture keeps the word positions of a real screenshot with made-up names and numbers (C-026).
/// </summary>
public class RailAppCardTests
{
    private static readonly DateOnly Imported = new(2026, 9, 24);

    private static List<LayoutWord> Words(Func<LayoutWord, LayoutWord?>? change = null) =>
        File.ReadAllLines(Path.Combine("samples", "railone-app-card.words.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(p => (Left: Num(p[0]), Top: Num(p[1]), Width: Num(p[2]), Height: Num(p[3]), Text: p[4]))
            .Select(w => new LayoutWord(w.Text, w.Left, w.Top, w.Left + w.Width, w.Top + w.Height))
            .Select(w => change is null ? w : change(w))
            .OfType<LayoutWord>()
            .ToList();

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>The screenshot goes through the same path as on the phone: OCR words → layout → parser.</summary>
    private static async Task<ParsedDocument> Import(List<LayoutWord> words)
    {
        var extractor = new ContentExtractor(new FixedWords(words), new NoBarcodes(), new NoPages());
        var content = await extractor.ExtractAsync([1, 2, 3], MediaTypes.Png, "Screenshot_railone.png");
        Assert.NotNull(content.Layout);
        return TravelDocumentParser.Parse(content, Imported);
    }

    private static string? Detail(ParsedDocument doc, string label) => doc.Details.FirstOrDefault(d => d.Label == label)?.Value;

    [Fact]
    public async Task Reads_the_journey_from_the_placement_of_an_unlabelled_card()
    {
        var doc = await Import(Words());

        Assert.Equal(DocumentType.Train, doc.Type);
        Assert.Equal("4100000001", doc.Pnr);
        var leg = Assert.Single(doc.Segments);
        Assert.Equal(("YPR", "KRNT"), (leg.From.Code, leg.To!.Code));
        Assert.Equal(("Yesvantpur", "Kurnool"), (leg.From.City, leg.To.City));
        Assert.Equal("Asia/Kolkata", leg.From.TimeZoneId);
        Assert.Equal(new DateTime(2026, 10, 5, 14, 45, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2026, 10, 5, 19, 23, 0), leg.Arrives!.Value.Local);
        Assert.Equal("20704", leg.ServiceNumber);
        Assert.Equal("KCG VANDE BHARAT", leg.ServiceName);
        Assert.Equal("Chair car (CC)", leg.TravelClass);
        Assert.Equal("C2/17", leg.Seat);
        Assert.False(doc.NeedsReview);
    }

    [Fact]
    public async Task Each_passenger_gets_their_own_status_and_the_booker_is_not_a_passenger()
    {
        var doc = await Import(Words());

        Assert.Equal(["SANTHOSH KUMAR", "RIA KUMAR"], doc.PassengerNames); // "Thank You Ria Kumar" is the account holder
        Assert.Equal("Confirmed · Coach C2 · Seat 17", Detail(doc, "SANTHOSH KUMAR"));
        Assert.Equal("Confirmed · Coach C2 · Seat 18", Detail(doc, "RIA KUMAR"));
        Assert.Equal("Confirmed", Detail(doc, "Status"));
        Assert.Equal("General", Detail(doc, "Quota"));
        Assert.Equal("100000000000001", Detail(doc, "Transaction ID"));
        Assert.Equal("Thu, 24 Sep 26", Detail(doc, "Booked on"));
    }

    [Fact]
    public async Task A_missed_departure_time_is_worked_out_from_the_arrival_and_journey_length()
    {
        // What Windows OCR made of the real screenshot: the large "14:45" was missed and "5 Oct" read as "S Oct".
        var doc = await Import(Words(w => w.Text switch
        {
            "14:45" => null,
            "5" when w.Left < 300 => w with { Text = "S" },
            _ => w,
        }));

        var leg = Assert.Single(doc.Segments);
        Assert.Equal(new DateTime(2026, 10, 5, 14, 45, 0), leg.Departs!.Value.Local);
        Assert.Equal(new DateTime(2026, 10, 5, 19, 23, 0), leg.Arrives!.Value.Local);
    }

    [Theory]
    [InlineData("1 Adult, 0 Child | CC | GENERAL | YESVANTPUR...", "CC", "General")]
    [InlineData("2 Adult, 1 Child I 3A I TATKAL I NEW DELHI", "3A", "Tatkal")]
    [InlineData("1 Adult | SL | PREMIUM TATKAL", "SL", "Premium Tatkal")]
    public void Summary_line_gives_class_and_quota(string line, string cls, string quota)
    {
        var facts = TextFacts.Analyze(line, Imported);
        Assert.Equal((cls, quota), TravelDocumentParser.RailSummary(facts));
    }

    private sealed class FixedWords(List<LayoutWord> words) : ITextRecognizer
    {
        public Task<string> ReadTextAsync(byte[] image, CancellationToken cancellationToken = default) => Task.FromResult("");

        public Task<RecognizedText> ReadWordsAsync(byte[] image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecognizedText(string.Join(' ', words.Select(w => w.Text)), words));
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
