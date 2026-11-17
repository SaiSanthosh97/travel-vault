using TravelVault.App.Services;
using TravelVault.Core.Vault;

namespace TravelVault.App.Views;

/// <summary>A view model that loads its data when its page appears.</summary>
public interface ILoads
{
    Task LoadAsync();
}

/// <summary>
/// Base page: loads its view model when shown, and reloads it when the vault changes (e.g. a sync brought something
/// new) while the page is visible. Optionally blocks screenshots on this screen.
/// </summary>
public class VaultPage : ContentPage
{
    private bool _visible;

    /// <summary>Block screenshots while this page is shown (identity documents, recovery code).</summary>
    public bool Secure { get; set; }

    protected static VaultHost Host => IPlatformApplication.Current!.Services.GetRequiredService<VaultHost>();

    protected static AppSettings Settings => IPlatformApplication.Current!.Services.GetRequiredService<AppSettings>();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        Host.Changed += OnVaultChanged;
        Services.Activity.SetSecure(Secure, Settings);
        await SafeLoadAsync();
    }

    /// <summary>
    /// OnAppearing is "async void": an exception there would crash the whole app. A screen that fails to load
    /// shows a message instead and is logged, so the rest of the app keeps working.
    /// </summary>
    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Android.Util.Log.Error("TravelVault", $"{GetType().Name} failed to load: {ex}");
            Dialogs.Toast(global::TravelVault.App.Resources.Strings.AppResources.Common_LoadFailed);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
        Host.Changed -= OnVaultChanged;
        Services.Activity.SetSecure(false, Settings);
    }

    protected virtual Task LoadAsync() => BindingContext is ILoads loads && Host.IsOpen ? loads.LoadAsync() : Task.CompletedTask;

    private async void OnVaultChanged(VaultChange change)
    {
        if (_visible && change.FromSync)
        {
            await SafeLoadAsync();
        }
    }
}
