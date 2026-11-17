using TravelVault.App.Resources.Strings;
using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;
using TravelVault.Core.Vault;
using TravelVault.Device.Files;

namespace TravelVault.App.Services;

/// <summary>
/// View, save and share an original file (D51, FR-A1–A3). Viewing decrypts into memory only. "Save to phone" writes a
/// readable copy to Downloads after a warning. Sharing writes a temporary copy into the app's private cache for the
/// share sheet; those copies are deleted the next time the app starts.
/// </summary>
public sealed class FileActions(VaultHost host, Dialogs dialogs)
{
    private static string ShareFolder => Path.Combine(FileSystem.CacheDirectory, "share");

    /// <returns>The file's bytes, or null when it isn't on this phone yet (then it has been requested from family).</returns>
    public async Task<(byte[] Content, FileVersion File)?> OpenAsync(TravelDocument document, string? fileVersionId = null)
    {
        var opened = await host.Services.DocumentService.OpenFileAsync(document.Id, fileVersionId);
        if (opened.Availability == FileAvailability.Present && opened.Content is { } content)
        {
            return (content, opened.File);
        }

        Dialogs.Toast(AppResources.File_Requested);
        return null;
    }

    public async Task SaveToPhoneAsync(TravelDocument document)
    {
        if (!await dialogs.ConfirmAsync(AppResources.File_SaveTitle, AppResources.File_SaveWarning, AppResources.File_SaveConfirm))
        {
            return;
        }

        if (await OpenAsync(document) is not { } file)
        {
            return;
        }

        if (!OperatingSystem.IsAndroidVersionAtLeast(29) && await Permissions.RequestAsync<Permissions.StorageWrite>() != PermissionStatus.Granted)
        {
            return;
        }

        var where = await Downloads.SaveAsync(file.Content, FileName(document, file.File), file.File.MediaType);
        Dialogs.Toast(string.Format(AppResources.File_Saved, where));
    }

    public async Task ShareAsync(TravelDocument document)
    {
        if (await OpenAsync(document) is not { } file)
        {
            return;
        }

        await ShareBytesAsync(file.Content, FileName(document, file.File), document.Title);
    }

    public static async Task ShareBytesAsync(byte[] content, string fileName, string title)
    {
        Directory.CreateDirectory(ShareFolder);
        var path = Path.Combine(ShareFolder, fileName);
        await File.WriteAllBytesAsync(path, content);
        await Share.Default.RequestAsync(new ShareFileRequest(title, new ShareFile(path)));
    }

    /// <summary>Deletes readable copies left from earlier shares (called at start-up).</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(ShareFolder))
            {
                Directory.Delete(ShareFolder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string FileName(TravelDocument document, FileVersion file)
    {
        var name = string.Concat(document.Title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : ' ')).Trim();
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return (name.Length == 0 ? "document" : name) + MediaTypes.Extension(file.MediaType);
    }
}
