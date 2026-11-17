using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.Views;
using TravelVault.Device.BackgroundWork;
using TravelVault.Device.Reminders;

namespace TravelVault.App.ViewModels;

/// <summary>Security, reminders, storage, updates, about, erase everything.</summary>
public sealed partial class SettingsViewModel(
    VaultHost host,
    AppSettings settings,
    LockService lockService,
    ReminderService reminders,
    UpdateService updates,
    Dialogs dialogs) : ObservableObject, ILoads
{
    private static readonly (string Label, int Seconds)[] LockTimes =
    [
        (AppResources.Settings_LockNow, 0), (AppResources.Settings_Lock1, 60), (AppResources.Settings_Lock5, 300), (AppResources.Settings_Lock15, 900),
    ];

    private bool _loading;

    public IReadOnlyList<string> LockTimeNames { get; } = LockTimes.Select(t => t.Label).ToList();

    [ObservableProperty]
    public partial bool AppLock { get; set; }

    [ObservableProperty]
    public partial bool NoScreenLock { get; set; }

    [ObservableProperty]
    public partial int LockTimeIndex { get; set; }

    [ObservableProperty]
    public partial bool StrictMode { get; set; }

    [ObservableProperty]
    public partial bool AllFamilyReminders { get; set; }

    [ObservableProperty]
    public partial string ReminderStatus { get; set; } = "";

    [ObservableProperty]
    public partial string StorageText { get; set; } = "";

    [ObservableProperty]
    public partial string KeyText { get; set; } = "";

    [ObservableProperty]
    public partial string VersionText { get; set; } = "";

    [ObservableProperty]
    public partial string YourName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            await LoadCoreAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadCoreAsync()
    {
        var s = host.Services;
        YourName = s.Session.DisplayName;
        AppLock = settings.AppLock;
        NoScreenLock = !lockService.PhoneHasScreenLock;
        LockTimeIndex = Math.Max(0, Array.FindIndex(LockTimes, t => t.Seconds == (int)settings.LockAfter.TotalSeconds));
        StrictMode = settings.StrictMode;
        AllFamilyReminders = settings.AllFamilyReminders;
        ReminderStatus = !TravelReminders.NotificationsAllowed ? AppResources.Settings_NotificationsOff
            : !TravelReminders.CanRingOnTime ? AppResources.Settings_NotOnTime
            : AppResources.Settings_RemindersOk;
        var size = 0L;
        foreach (var family in await s.Families.ListAsync())
        {
            size += s.Files.SizeOfFamily(family.Id);
        }

        StorageText = string.Format(AppResources.Settings_Storage, Display.Size(size));
        KeyText = host.HardwareBackedKey ? AppResources.Settings_KeyHardware : AppResources.Settings_KeySoftware;
        VersionText = string.Format(AppResources.Settings_Version, AppInfo.Current.VersionString);
    }

    partial void OnAppLockChanged(bool value)
    {
        if (!_loading)
        {
            settings.AppLock = value;
        }
    }

    partial void OnLockTimeIndexChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            settings.LockAfter = TimeSpan.FromSeconds(LockTimes[value].Seconds);
        }
    }

    partial void OnStrictModeChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        settings.StrictMode = value;
        BackgroundWork.SetEnabled(!value);
        Services.Activity.SetSecure(value, settings);
    }

    partial void OnAllFamilyRemindersChanged(bool value)
    {
        if (!_loading)
        {
            settings.AllFamilyReminders = value;
            _ = reminders.ReplanAsync();
        }
    }

    [RelayCommand]
    private async Task FixRemindersAsync()
    {
        if (!TravelReminders.NotificationsAllowed)
        {
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        else if (!TravelReminders.CanRingOnTime && Activity.Current is { } activity)
        {
            TravelReminders.OpenOnTimeSettings(activity);
        }

        await reminders.ReplanAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await dialogs.PromptAsync(AppResources.Settings_YourName, AppResources.Welcome_NameHelp, YourName, maxLength: 40);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await host.Services.Identity.RenameAsync(name);
            host.Services.Session.DisplayName = name.Trim();
            YourName = name.Trim();
        }
    }

    [RelayCommand]
    private Task RecentlyDeletedAsync() => Nav.GoAsync("deleted");

    [RelayCommand]
    private Task TravelersAsync() => Nav.GoAsync("travelers");

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        try
        {
            if (await updates.CheckNowAsync() is { } update)
            {
                await updates.OfferAsync(update);
            }
            else
            {
                await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_None);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_Failed);
        }
    }

    [RelayCommand]
    private Task AboutAsync() => dialogs.AlertAsync(AppResources.Settings_About, AppResources.Settings_AboutText);

    [RelayCommand]
    private async Task EraseAsync()
    {
        if (!await dialogs.ConfirmAsync(AppResources.Settings_EraseTitle, AppResources.Settings_EraseText, AppResources.Settings_EraseConfirm)
            || !await lockService.ConfirmIdentityAsync())
        {
            return;
        }

        var typed = await dialogs.PromptAsync(AppResources.Settings_EraseTitle, AppResources.Settings_EraseType, maxLength: 10);
        if (!string.Equals(typed?.Trim(), AppResources.Settings_EraseWord, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        host.EraseEverything();
        await Nav.GoAsync("//welcome");
    }
}
