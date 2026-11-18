using TravelVault.Core.Documents;

namespace TravelVault.Core.Parsing;

public enum BarcodeFormat
{
    Unknown = 0,
    Pdf417,
    Aztec,
    QrCode,
    DataMatrix,
    Code128,
    Other,
}

public sealed record Barcode(BarcodeFormat Format, string Value);

/// <summary>
/// What was read from a file: its text (PDF text layer or OCR), any barcodes, and where each word sits on the page
/// (<see cref="Layout"/>; null for plain text, which is then read as a character grid).
/// </summary>
public sealed record ExtractedContent(string Text, IReadOnlyList<Barcode> Barcodes, string? FileName = null)
{
    public TextLayout? Layout { get; init; }

    public static readonly ExtractedContent Empty = new("", []);
}

/// <summary>
/// The parser's proposal for a new document. The import service turns it into a <see cref="TravelDocument"/>
/// after matching passenger names to travelers. Every field carries a confidence for the review screen.
/// </summary>
public sealed class ParsedDocument
{
    public DocumentType Type { get; set; }

    public string? Provider { get; set; }

    public string? Pnr { get; set; }

    public string? BookingReference { get; set; }

    public List<Segment> Segments { get; } = [];

    /// <summary>Names as printed, in order.</summary>
    public List<string> PassengerNames { get; } = [];

    public IdentityInfo? Identity { get; set; }

    /// <summary>Extra facts worth showing on the ticket (quota, booking status, terminal, gate…), in print order.</summary>
    public List<DocumentDetail> Details { get; } = [];

    public void AddDetail(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Details.Any(d => d.Label == label))
        {
            Details.Add(new DocumentDetail(label, value.Trim()));
        }
    }

    /// <summary>Raw boarding-pass barcode data, when found (used to link to a ticket).</summary>
    public BoardingPassData? BoardingPass { get; set; }

    public Dictionary<string, Confidence> Confidence { get; } = [];

    /// <summary>Things the user should know ("Date order assumed day/month").</summary>
    public List<string> Warnings { get; } = [];

    public void Set(string field, Confidence confidence) => Confidence[field] = confidence;

    /// <summary>True when the essentials for this type were found with at least medium confidence.</summary>
    public bool NeedsReview =>
        Confidence.Values.Any(c => c == Documents.Confidence.Low)
        || Type == DocumentType.Other
        || (Type is DocumentType.Flight or DocumentType.Train or DocumentType.Bus or DocumentType.BoardingPass && Segments.Count == 0)
        || (Type is DocumentType.Hotel or DocumentType.Event && Segments.All(s => s.Departs is null));
}
