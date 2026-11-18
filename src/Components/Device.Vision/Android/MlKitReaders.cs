using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using Android.Runtime;
using TravelVault.Core.Parsing;
using Xamarin.Google.MLKit.Vision.BarCode;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
using AndroidApp = Android.App.Application;
using MlBarcode = Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode;

namespace TravelVault.Device.Vision;

/// <summary>
/// Image bytes → ML Kit input. The bytes are written to a private temporary file only so ML Kit can read the photo's
/// EXIF rotation (portrait photos read the right way up); the file is deleted immediately afterwards.
/// </summary>
internal static class Images
{
    public static async Task<T> WithInputImageAsync<T>(byte[] image, Func<InputImage, Task<T>> work)
    {
        var path = System.IO.Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "ocr-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, image);
        try
        {
            using var input = InputImage.FromFilePath(AndroidApp.Context, Android.Net.Uri.FromFile(new Java.IO.File(path))!);
            return await work(input);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Reads printed Latin-script text from a photo or rendered PDF page with Google ML Kit, on the phone.</summary>
public sealed class MlKitTextRecognizer : Core.Parsing.ITextRecognizer
{
    public async Task<string> ReadTextAsync(byte[] image, CancellationToken cancellationToken = default) =>
        (await ReadWordsAsync(image, cancellationToken)).Text;

    /// <summary>
    /// Text plus every word's box in image pixels. The boxes let the form reader match a label to the value printed
    /// under it, as rail and airline apps lay out their ticket screens ("PNR" in small print, the number below).
    /// </summary>
    public Task<RecognizedText> ReadWordsAsync(byte[] image, CancellationToken cancellationToken = default) =>
        Images.WithInputImageAsync(image, async input =>
        {
            using var recognizer = TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);
            try
            {
                var result = (await recognizer.Process(input).AsTask().WaitAsync(cancellationToken))?.JavaCast<Text>();
                if (result is null)
                {
                    return new RecognizedText("", []);
                }

                var lines = result.TextBlocks.SelectMany(b => b.Lines).ToList();
                var words = new List<LayoutWord>();
                foreach (var line in lines)
                {
                    foreach (var element in line.Elements)
                    {
                        if (element.BoundingBox is { } box && !string.IsNullOrWhiteSpace(element.Text))
                        {
                            words.Add(new LayoutWord(element.Text, box.Left, box.Top, box.Right, box.Bottom));
                        }
                    }
                }

                // Line by line, block by block: keeps "Label: value" pairs together for the text rules.
                return new RecognizedText(string.Join('\n', lines.Select(l => l.Text)), words);
            }
            finally
            {
                recognizer.Close();
            }
        });
}

/// <summary>Finds barcodes (PDF417 / Aztec / QR boarding passes, ticket codes) with Google ML Kit, on the phone.</summary>
public sealed class MlKitBarcodeScanner : Core.Parsing.IBarcodeScanner
{
    public Task<IReadOnlyList<Core.Parsing.Barcode>> ScanAsync(byte[] image, CancellationToken cancellationToken = default) =>
        Images.WithInputImageAsync<IReadOnlyList<Core.Parsing.Barcode>>(image, async input =>
        {
            using var scanner = BarcodeScanning.Client;
            try
            {
                var result = await scanner.Process(input).AsTask().WaitAsync(cancellationToken);
                var barcodes = result?.JavaCast<JavaList>();
                if (barcodes is null)
                {
                    return [];
                }

                return barcodes
                    .Cast<Java.Lang.Object>()
                    .Select(o => o.JavaCast<MlBarcode>())
                    .Where(b => !string.IsNullOrEmpty(b?.RawValue))
                    .Select(b => new Core.Parsing.Barcode(FormatOf(b!.Format), b.RawValue!))
                    .ToList();
            }
            finally
            {
                scanner.Close();
            }
        });

    private static BarcodeFormat FormatOf(int format) => format switch
    {
        MlBarcode.FormatPdf417 => BarcodeFormat.Pdf417,
        MlBarcode.FormatAztec => BarcodeFormat.Aztec,
        MlBarcode.FormatQrCode => BarcodeFormat.QrCode,
        MlBarcode.FormatDataMatrix => BarcodeFormat.DataMatrix,
        MlBarcode.FormatCode128 => BarcodeFormat.Code128,
        _ => BarcodeFormat.Other,
    };
}

/// <summary>
/// Renders PDF pages to PNG with Android's PdfRenderer (part of the OS, runs no scripts). Used for barcode scanning,
/// OCR of scanned PDFs and the in-app viewer. The decrypted PDF exists as a private temp file only while rendering.
/// </summary>
public sealed class AndroidPdfRasterizer : IPdfRasterizer
{
    /// <summary>Longest side of a rendered page: enough for OCR and barcodes, small enough for memory.</summary>
    public int MaxPixels { get; init; } = 2200;

    public async Task<IReadOnlyList<byte[]>> RenderPagesAsync(byte[] pdf, int maxPages, CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "pdf-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, pdf, cancellationToken);
        try
        {
            return await Task.Run(() => Render(path, maxPages, cancellationToken), cancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public int PageCount(byte[] pdf)
    {
        var path = System.IO.Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "pdf-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, pdf);
        try
        {
            using var descriptor = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly)!;
            using var renderer = new PdfRenderer(descriptor);
            return renderer.PageCount;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private List<byte[]> Render(string path, int maxPages, CancellationToken cancellationToken)
    {
        var pages = new List<byte[]>();
        using var descriptor = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly)!;
        using var renderer = new PdfRenderer(descriptor); // throws for password-protected PDFs (handled by the caller)
        for (var i = 0; i < Math.Min(maxPages, renderer.PageCount); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = renderer.OpenPage(i);
            var scale = Math.Min(3.0, MaxPixels / (double)Math.Max(page.Width, page.Height));
            var width = Math.Max(1, (int)(page.Width * scale));
            var height = Math.Max(1, (int)(page.Height * scale));
            using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
            bitmap.EraseColor(Color.White); // PDFs assume a white page
            page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
            using var stream = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
            pages.Add(stream.ToArray());
        }

        return pages;
    }
}
