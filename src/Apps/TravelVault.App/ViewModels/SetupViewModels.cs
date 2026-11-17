using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.Core.Backup;
using TravelVault.Core.Security;

namespace TravelVault.App.ViewModels;

/// <summary>Decides where the app starts: setup, restore, recovery-code confirmation, lock, or home.</summary>
public sealed class StartViewModel(VaultHost host, AppSettings settings, LockService lockService, UpdateService updates)
{
    public async Task RouteAsync()
    {
        if (!host.IsSetUp)
        {
            await Nav.GoAsync("//welcome");
            return;
        }

        if (!await host.EnsureOpenAsync())
        {
            // The Keystore key is gone (factory reset, app data copied): only a backup can bring the data back.
            await Nav.GoAsync("//welcome?lost=1");
            return;
        }

        if (!settings.RecoveryCodeConfirmed)
        {
            await Nav.GoAsync("//recovery");
            return;
        }

        if (lockService.IsEnabled && !await lockService.UnlockAsync())
        {
            await ((App)Application.Current!).ShowLockAsync();
        }

        await Nav.HomeAsync();
        await ((App)Application.Current!).AfterUnlockAsync();
        _ = updates.OfferIfDueAsync();
    }
}

/// <summary>J1: first launch — "your name", then the recovery code. Or restore a backup on a new phone (J12).</summary>
public sealed partial class WelcomeViewModel(VaultHost host, AppSettings settings, Dialogs dialogs) : ObservableObject, IQueryAttributable
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool KeyLost { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => KeyLost = query.ContainsKey("lost");

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            await dialogs.AlertAsync(AppResources.Welcome_Title, AppResources.Welcome_NameNeeded);
            return;
        }

        if (KeyLost && !await dialogs.ConfirmAsync(AppResources.Welcome_StartOverTitle, AppResources.Welcome_StartOverText, AppResources.Welcome_StartOver))
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (KeyLost)
            {
                host.EraseEverything();
            }

            await host.CreateAsync(Name.Trim());
            settings.RecoveryCodeConfirmed = false;
            await Nav.GoAsync("//recovery");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = AppResources.Restore_Pick });
        if (file is null)
        {
            return;
        }

        var text = await dialogs.PromptAsync(AppResources.Restore_Title, AppResources.Restore_EnterCode, maxLength: 60);
        if (text is null)
        {
            return;
        }

        if (!RecoveryCode.TryParse(text, out var code))
        {
            await dialogs.AlertAsync(AppResources.Restore_Title, AppResources.Restore_BadCode);
            return;
        }

        IsBusy = true;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            await host.RestoreAsync(copy, code!);
            Dialogs.Toast(AppResources.Restore_Done);
            await Nav.HomeAsync();
            await host.Services.Sync.SyncAllAsync();
        }
        catch (WrongRecoveryCodeException)
        {
            await dialogs.AlertAsync(AppResources.Restore_Title, AppResources.Restore_WrongCode);
        }
        catch (InvalidDataException)
        {
            await dialogs.AlertAsync(AppResources.Restore_Title, AppResources.Restore_NotBackup);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Shows the recovery code once after setup (and again from Settings after unlocking). Screenshots blocked.</summary>
public sealed partial class RecoveryCodeViewModel(VaultHost host, AppSettings settings, Dialogs dialogs) : ObservableObject
{
    [ObservableProperty]
    public partial string Code { get; set; } = "";

    [ObservableProperty]
    public partial bool WrittenDown { get; set; }

    public async Task LoadAsync() =>
        Code = (await host.Services.Identity.LoadRecoveryCodeAsync())?.Display().Replace("-", " - ", StringComparison.Ordinal) ?? "";

    [RelayCommand]
    private async Task DoneAsync()
    {
        if (!WrittenDown)
        {
            await dialogs.AlertAsync(AppResources.Recovery_Title, AppResources.Recovery_Confirm);
            return;
        }

        var first = !settings.RecoveryCodeConfirmed;
        settings.RecoveryCodeConfirmed = true;
        await (first ? Nav.HomeAsync() : Nav.GoAsync("//main/settings"));
    }
}
