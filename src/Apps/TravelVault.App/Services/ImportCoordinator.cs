using TravelVault.App.Resources.Strings;
using TravelVault.Core.Vault;

namespace TravelVault.App.Services;

/// <summary>
/// Runs the import use case from the UI: shared files, camera, picked files, pasted text. Every result is
/// confirmed on the short ticket form first (D63), then grouped into trips (D65).
/// </summary>
public sealed class ImportCoordinator(VaultHost host, AppSettings settings, Dialogs dialogs)
{
    private bool _running;

    /// <summary>The draft waiting on the review screen.</summary>
    public ImportDraft? CurrentDraft { get; private set; }

    /// <summary>Imports everything waiting in the share inbox, one after another.</summary>
    public async Task ProcessInboxAsync()
    {
        if (_running || !host.IsOpen)
        {
            return;
        }

        _running = true;
        try
        {
            while (Inbox.TakeShared() is { } item)
            {
                await ImportAsync(item.Content, item.FileName);
                if (CurrentDraft is not null)
                {
                    return; // the review screen is open; the next item waits until it's done
                }
            }

            if (Inbox.TakeDocumentToOpen() is { } id)
            {
                await Nav.DocumentAsync(id);
            }
        }
        finally
        {
            _running = false;
        }
    }

    public async Task ImportAsync(byte[] content, string? fileName)
    {
        if (await CurrentFamilyAsync() is not { } familyId)
        {
            await dialogs.AlertAsync(AppResources.Import_Title, AppResources.Import_NoFamily);
            return;
        }

        ImportResult result;
        try
        {
            result = await host.Services.Import.PrepareAsync(familyId, content, fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Android.Util.Log.Warn("TravelVault", $"Import failed: {ex}");
            await dialogs.AlertAsync(AppResources.Import_Title, AppResources.Import_Failed);
            return;
        }

        switch (result.Status)
        {
            case ImportStatus.AlreadyInVault:
                Dialogs.Toast(string.Format(AppResources.Import_AlreadyInVault, result.Existing!.Title));
                await Nav.DocumentAsync(result.Existing.Id);
                return;
            case ImportStatus.Unsupported:
                await dialogs.AlertAsync(AppResources.Import_Title, AppResources.Import_Unsupported);
                return;
            case ImportStatus.TooLarge:
                await dialogs.AlertAsync(AppResources.Import_Title, AppResources.Import_TooLarge);
                return;
        }

        // Every ticket is confirmed on the short form (D63): what was read is pre-filled, nothing is saved unseen.
        CurrentDraft = result.Draft!;
        await Nav.GoAsync("review");
    }

    public void DraftFinished()
    {
        CurrentDraft = null;
        _ = MainThread.InvokeOnMainThreadAsync(ProcessInboxAsync);
    }

    /// <summary>
    /// The trip the next saved ticket joins: set when the person chose "add the return / next ticket" or
    /// "add a ticket to this trip" (D65). Cleared once used or when they stop adding.
    /// </summary>
    public string? PendingTripId { get; set; }

    /// <summary>Asks where the ticket comes from (camera, photos, a file, copied text) and imports each pick.</summary>
    public async Task AddAsync()
    {
        var choice = await dialogs.ChooseAsync(AppResources.Add_Title, AppResources.Add_Camera, AppResources.Add_Photo, AppResources.Add_File, AppResources.Add_Paste);
        try
        {
            if (choice == AppResources.Add_Camera)
            {
                if (await MediaPicker.Default.CapturePhotoAsync() is { } photo)
                {
                    await ImportFileAsync(photo);
                }
            }
            else if (choice == AppResources.Add_Photo)
            {
                foreach (var picked in await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 10 }) ?? [])
                {
                    await ImportFileAsync(picked);
                }
            }
            else if (choice == AppResources.Add_File)
            {
                var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
                {
                    PickerTitle = AppResources.Add_File,
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.Android] = ["application/pdf", "image/*", "text/plain"] }),
                });
                foreach (var file in files ?? [])
                {
                    if (file is not null)
                    {
                        await ImportFileAsync(file);
                    }
                }
            }
            else if (choice == AppResources.Add_Paste)
            {
                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    await dialogs.AlertAsync(AppResources.Add_Title, AppResources.Add_ClipboardEmpty);
                    return;
                }

                await ImportAsync(System.Text.Encoding.UTF8.GetBytes(text), null);
            }
            else
            {
                PendingTripId = null; // cancelled the picker: stop adding to the trip
            }
        }
        catch (PermissionException)
        {
            await dialogs.AlertAsync(AppResources.Add_Title, AppResources.Add_NoPermission);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A photo or file that can't be read (moved, cloud-only, no camera…) must not close the app.
            Android.Util.Log.Warn("TravelVault", $"Adding a ticket failed: {ex}");
            await dialogs.AlertAsync(AppResources.Import_Title, AppResources.Import_Failed);
        }
    }

    private async Task ImportFileAsync(FileResult file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        await ImportAsync(buffer.ToArray(), file.FileName);
    }

    /// <summary>The open family; picks the first one if the remembered one is gone.</summary>
    public async Task<string?> CurrentFamilyAsync()
    {
        var families = await host.Services.Families.ListAsync();
        if (families.FirstOrDefault(f => f.Id == settings.CurrentFamilyId) is { } current)
        {
            return current.Id;
        }

        settings.CurrentFamilyId = families.FirstOrDefault()?.Id;
        return settings.CurrentFamilyId;
    }
}
