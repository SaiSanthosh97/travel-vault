using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;

namespace TravelVault.App.ViewModels;

/// <summary>Decides where the app starts: setup, lock, or home.</summary>
public sealed class StartViewModel(VaultHost host, LockService lockService, UpdateService updates)
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
            // The Keystore key is gone (factory reset, app data copied): start over and rejoin the family.
            await Nav.GoAsync("//welcome?lost=1");
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

/// <summary>J1: first launch, "your name". A new phone joins the family again to get its data back (C-020).</summary>
public sealed partial class WelcomeViewModel(VaultHost host, Dialogs dialogs) : ObservableObject, IQueryAttributable
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
            await Nav.HomeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

