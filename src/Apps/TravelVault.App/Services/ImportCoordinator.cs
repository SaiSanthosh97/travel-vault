using TravelVault.App.Resources.Strings;
using TravelVault.Core.Vault;

namespace TravelVault.App.Services;

/// <summary>
/// Runs the import use case from the UI: shared files, camera, picked files, pasted text. Confident results are
/// saved straight away with a short message; anything uncertain opens the review screen (J3, J4, J6).
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

        var draft = result.Draft!;
        if (draft.CanSaveWithoutReview)
        {
            var saved = await host.Services.Import.SaveAsync(draft);
            Dialogs.Toast(string.Format(AppResources.Import_Saved, saved.Title));
            await Nav.DocumentAsync(saved.Id);
            return;
        }

        CurrentDraft = draft;
        await Nav.GoAsync("review");
    }

    public void DraftFinished()
    {
        CurrentDraft = null;
        _ = MainThread.InvokeOnMainThreadAsync(ProcessInboxAsync);
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
