using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace TravelVault.Core.Parsing;

/// <summary>Reads text from an image, on the phone (Android: Google ML Kit).</summary>
public interface ITextRecognizer
{
    Task<string> ReadTextAsync(byte[] image, CancellationToken cancellationToken = default);

    /// <summary>The text plus each word's box, so labels can be matched to the values under them. Recognisers that can't
    /// give positions return the text only.</summary>
    async Task<RecognizedText> ReadWordsAsync(byte[] image, CancellationToken cancellationToken = default) =>
        new(await ReadTextAsync(image, cancellationToken), []);
}

/// <summary>OCR output: the text and, when available, each word with its box in image pixels.</summary>
public sealed record RecognizedText(string Text, IReadOnlyList<LayoutWord> Words);

/// <summary>Finds barcodes in an image (Android: Google ML Kit): PDF417/Aztec/QR boarding passes, ticket QR codes.</summary>
public interface IBarcodeScanner
{
    Task<IReadOnlyList<Barcode>> ScanAsync(byte[] image, CancellationToken cancellationToken = default);
}

/// <summary>Renders PDF pages to images (Android: PdfRenderer, sandboxed by the OS).</summary>
public interface IPdfRasterizer
{
    Task<IReadOnlyList<byte[]>> RenderPagesAsync(byte[] pdf, int maxPages, CancellationToken cancellationToken = default);
}

/// <summary>Detects a file's real type from its first bytes ("magic numbers"), never from its name.</summary>
public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
    public const string Heic = "image/heic";
    public const string Gif = "image/gif";
    public const string Text = "text/plain";

    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("%PDF-"u8))
        {
            return Pdf;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return Jpeg;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8) && (data.Slice(8, 4).SequenceEqual("heic"u8) || data.Slice(8, 4).SequenceEqual("heix"u8) || data.Slice(8, 4).SequenceEqual("mif1"u8)))
        {
            return Heic;
        }

        if (data.StartsWith("GIF8"u8))
        {
            return Gif;
        }

        return LooksLikeText(data) ? Text : null;
    }

    public static bool IsImage(string? mediaType) => mediaType is not null && mediaType.StartsWith("image/", StringComparison.Ordinal);

    public static string Extension(string mediaType) => mediaType switch
    {
        Pdf => ".pdf",
        Jpeg => ".jpg",
        Png => ".png",
        Webp => ".webp",
        Heic => ".heic",
        Gif => ".gif",
        _ => ".txt",
    };

    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        var sample = data[..Math.Min(data.Length, 4096)];
        if (sample.Contains((byte)0))
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sample.Length == data.Length ? sample : sample[..LastCompleteUtf8(sample)]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int LastCompleteUtf8(ReadOnlySpan<byte> s)
    {
        var end = s.Length;
        while (end > 0 && (s[end - 1] & 0xC0) == 0x80)
        {
            end--; // drop a cut-off multi-byte character at the sample edge
        }

        return end > 0 && s[end - 1] >= 0xC0 ? end - 1 : end;
    }
}

/// <summary>
/// Gets text and barcodes out of any supported file:
/// PDF → text layer (fast, exact) + barcodes from rendered pages; scanned PDFs (little text) → OCR of rendered pages.
/// Image → OCR + barcodes. Text → as is. Everything runs on the phone.
/// </summary>
public sealed class ContentExtractor(ITextRecognizer ocr, IBarcodeScanner scanner, IPdfRasterizer rasterizer)
{
    public const long MaxFileSize = 25 * 1024 * 1024;
    private const int MaxPages = 4;
    private const int MinUsefulTextLength = 80;

    public async Task<ExtractedContent> ExtractAsync(byte[] data, string mediaType, string? fileName, CancellationToken cancellationToken = default)
    {
        if (data.Length > MaxFileSize)
        {
            return ExtractedContent.Empty with { FileName = fileName };
        }

        if (mediaType == MediaTypes.Text)
        {
            return new ExtractedContent(Encoding.UTF8.GetString(data), [], fileName);
        }

        if (MediaTypes.IsImage(mediaType))
        {
            var recognized = await ocr.ReadWordsAsync(data, cancellationToken);
            var codes = await scanner.ScanAsync(data, cancellationToken);
            return WithLayout(recognized.Text, recognized.Words, codes, fileName);
        }

        if (mediaType != MediaTypes.Pdf)
        {
            return ExtractedContent.Empty with { FileName = fileName };
        }

        var pdfWords = PdfWords(data);
        var pdfText = pdfWords.Count > 0 ? TextLayout.FromWords(pdfWords).Text : PdfText(data);
        IReadOnlyList<byte[]> pages;
        try
        {
            pages = await rasterizer.RenderPagesAsync(data, MaxPages, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pages = []; // e.g. password-protected PDF: keep whatever text we got
        }

        var barcodes = new List<Barcode>();
        var ocrText = new StringBuilder();
        var ocrWords = new List<LayoutWord>();
        for (var i = 0; i < pages.Count; i++)
        {
            barcodes.AddRange(await scanner.ScanAsync(pages[i], cancellationToken));
            if (pdfText.Length < MinUsefulTextLength)
            {
                var recognized = await ocr.ReadWordsAsync(pages[i], cancellationToken);
                ocrText.AppendLine(recognized.Text);
                ocrWords.AddRange(recognized.Words.Select(w => w with { Page = i }));
            }
        }

        var distinct = barcodes.DistinctBy(b => b.Value).ToList();
        return pdfText.Length >= MinUsefulTextLength
            ? WithLayout(pdfText, pdfWords, distinct, fileName)
            : WithLayout((pdfText + "\n" + ocrText).Trim(), ocrWords, distinct, fileName);
    }

    /// <summary>With word positions, the text is rebuilt row by row (left to right) and the layout is kept for the form reader.</summary>
    private static ExtractedContent WithLayout(string text, IReadOnlyList<LayoutWord> words, IReadOnlyList<Barcode> barcodes, string? fileName)
    {
        if (words.Count == 0)
        {
            return new ExtractedContent(text, barcodes, fileName);
        }

        var layout = TextLayout.FromWords(words);
        return new ExtractedContent(layout.Text, barcodes, fileName) { Layout = layout };
    }

    /// <summary>Every word of the PDF text layer with its box (y measured from the top of the page). Empty for scanned PDFs.</summary>
    public static IReadOnlyList<LayoutWord> PdfWords(byte[] data)
    {
        try
        {
            using var document = PdfDocument.Open(data);
            var words = new List<LayoutWord>();
            var index = 0;
            foreach (var page in document.GetPages().Take(MaxPages * 2))
            {
                foreach (var word in page.GetWords())
                {
                    var box = word.BoundingBox;
                    words.Add(new LayoutWord(word.Text, box.Left, page.Height - box.Top, box.Right, page.Height - box.Bottom, index));
                }

                index++;
            }

            return words;
        }
        catch (Exception)
        {
            return []; // encrypted, damaged or unusual: the caller falls back to OCR of the rendered pages
        }
    }

    /// <summary>Text layer of a PDF in reading order, page by page. Empty for scanned or protected PDFs.</summary>
    public static string PdfText(byte[] data)
    {
        try
        {
            using var document = PdfDocument.Open(data);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages().Take(MaxPages * 2))
            {
                builder.AppendLine(ContentOrderTextExtractor.GetText(page));
            }

            return builder.ToString().Trim();
        }
        catch (Exception)
        {
            // Encrypted, damaged or unusual PDFs: fall back to OCR of the rendered pages. A broken file must
            // never stop the import — the user can still store it and type the details.
            return "";
        }
    }
}
