using Android.Content;
using Android.Provider;
using AndroidApp = Android.App.Application;

namespace TravelVault.Device.Files;

/// <summary>One item shared into the app: file bytes (with the sender's file name), or plain text.</summary>
public sealed record SharedItem(byte[] Content, string? FileName, bool IsText);

/// <summary>
/// Reads what another app shared with Travel Vault (Android share sheet: ACTION_SEND / ACTION_SEND_MULTIPLE, or
/// "Open with" ACTION_VIEW). Content is copied out of the sender's content URI immediately, with a size cap, because
/// the read permission the sender grants is temporary. The sender's MIME type is not trusted: the import detects
/// the real type from the bytes.
/// </summary>
public static class SharedContent
{
    public const long MaxBytes = 25 * 1024 * 1024;

    public static bool IsShare(Intent? intent) =>
        intent?.Action is Intent.ActionSend or Intent.ActionSendMultiple or Intent.ActionView;

    public static async Task<IReadOnlyList<SharedItem>> ReadAsync(Intent intent, CancellationToken cancellationToken = default)
    {
        var items = new List<SharedItem>();
        foreach (var uri in Uris(intent).Take(10))
        {
            if (await ReadUriAsync(uri, cancellationToken) is { } item)
            {
                items.Add(item);
            }
        }

        // Shared text (an SMS, an email body) when no file came with it.
        if (items.Count == 0 && intent.GetStringExtra(Intent.ExtraText) is { Length: > 0 } text)
        {
            items.Add(new SharedItem(System.Text.Encoding.UTF8.GetBytes(text[..Math.Min(text.Length, 200_000)]), null, IsText: true));
        }

        return items;
    }

    private static IEnumerable<Android.Net.Uri> Uris(Intent intent)
    {
        if (intent.Action == Intent.ActionView && intent.Data is { } data)
        {
            yield return data;
        }

        if (intent.Action == Intent.ActionSend && Extra(intent) is { } single)
        {
            yield return single;
        }

        if (intent.Action == Intent.ActionSendMultiple)
        {
            var list = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri)))
                : intent.GetParcelableArrayListExtra(Intent.ExtraStream);
            foreach (var item in list?.Cast<Android.Net.Uri>() ?? [])
            {
                yield return item;
            }
        }
    }

    private static Android.Net.Uri? Extra(Intent intent) =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? (Android.Net.Uri?)intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri)))
            : (Android.Net.Uri?)intent.GetParcelableExtra(Intent.ExtraStream);

    private static async Task<SharedItem?> ReadUriAsync(Android.Net.Uri uri, CancellationToken cancellationToken)
    {
        // Only content:// URIs from other apps' providers; never file:// paths (could point into our own private folder).
        if (uri.Scheme != ContentResolver.SchemeContent)
        {
            return null;
        }

        var resolver = AndroidApp.Context.ContentResolver!;
        try
        {
            await using var stream = resolver.OpenInputStream(uri);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes)
                {
                    return null;
                }
            }

            return new SharedItem(buffer.ToArray(), DisplayName(resolver, uri), IsText: false);
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.IO.IOException or IOException)
        {
            return null;
        }
    }

    private static string? DisplayName(ContentResolver resolver, Android.Net.Uri uri)
    {
        using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
        if (cursor is not null && cursor.MoveToFirst())
        {
            var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
            if (index >= 0)
            {
                return Path.GetFileName(cursor.GetString(index)); // never trust a path from another app
            }
        }

        return null;
    }
}

/// <summary>"Save to phone": writes a readable copy into the phone's Downloads folder (D51).</summary>
public static class Downloads
{
    /// <returns>A user-visible location description, e.g. "Downloads/Flight BLR-DEL.pdf".</returns>
    public static async Task<string> SaveAsync(byte[] content, string fileName, string mimeType, CancellationToken cancellationToken = default)
    {
        var safeName = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var resolver = AndroidApp.Context.ContentResolver!;
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, safeName);
            values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
            values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads);
            var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values) ?? throw new IOException("Could not create the file in Downloads.");
            await using var output = resolver.OpenOutputStream(uri) ?? throw new IOException("Could not write to Downloads.");
            await output.WriteAsync(content, cancellationToken);
            return $"Downloads/{safeName}";
        }

        // Android 8–9: the public Downloads folder (needs the storage permission, asked by the app before calling).
#pragma warning disable CA1422 // obsolete on newer Android, the only way on 8–9
        var folder = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
#pragma warning restore CA1422
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, safeName);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        return $"Downloads/{safeName}";
    }
}
