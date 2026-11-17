using TravelVault.App.Resources.Strings;

namespace TravelVault.App.Services;

/// <summary>Pop-up dialogs, so view models don't depend on pages (same pattern as Use It Before).</summary>
public sealed class Dialogs
{
    private static Page Page => Shell.Current?.CurrentPage ?? Application.Current!.Windows[0].Page!;

    public Task AlertAsync(string title, string message) => Page.DisplayAlertAsync(title, message, AppResources.Common_OK);

    public Task<bool> ConfirmAsync(string title, string message, string accept, string? cancel = null) =>
        Page.DisplayAlertAsync(title, message, accept, cancel ?? AppResources.Common_Cancel);

    /// <returns>The typed text, or null when cancelled.</returns>
    public Task<string?> PromptAsync(string title, string message, string? initialValue = null, Keyboard? keyboard = null, int maxLength = 200) =>
        Page.DisplayPromptAsync(title, message, AppResources.Common_OK, AppResources.Common_Cancel, initialValue: initialValue ?? "", maxLength: maxLength, keyboard: keyboard ?? Keyboard.Default);

    /// <returns>The chosen option, or null when cancelled.</returns>
    public async Task<string?> ChooseAsync(string title, params string[] options)
    {
        var choice = await Page.DisplayActionSheetAsync(title, AppResources.Common_Cancel, null, options);
        return choice is null || choice == AppResources.Common_Cancel ? null : choice;
    }

    /// <summary>A short message at the bottom of the screen (Android toast).</summary>
    public static void Toast(string text) => MainThread.BeginInvokeOnMainThread(() =>
        Android.Widget.Toast.MakeText(Platform.AppContext, text, Android.Widget.ToastLength.Long)?.Show());
}

/// <summary>Access to the Android activity for platform features (app lock, screen security, brightness).</summary>
public static class Activity
{
    public static AndroidX.Fragment.App.FragmentActivity? Current => Platform.CurrentActivity as AndroidX.Fragment.App.FragmentActivity;

    /// <summary>
    /// Screenshots are allowed (C-017, the user's choice): FLAG_SECURE is only applied in Strict mode, on every screen.
    /// <paramref name="sensitive"/> marks screens that would be protected if per-screen blocking is turned back on.
    /// </summary>
    public static void SetSecure(bool sensitive, AppSettings settings)
    {
        _ = sensitive;
        if (Current is { } activity)
        {
            Device.Security.ScreenSecurity.SetSecure(activity, settings.StrictMode);
        }
    }

    public static void SetBarcodeMode(bool on)
    {
        if (Current is { } activity)
        {
            Device.Security.ScreenSecurity.SetBarcodeMode(activity, on);
        }
    }
}

/// <summary>Shell navigation in one place.</summary>
public static class Nav
{
    public static Task GoAsync(string route, IDictionary<string, object>? parameters = null) =>
        MainThread.InvokeOnMainThreadAsync(() => parameters is null ? Shell.Current.GoToAsync(route) : Shell.Current.GoToAsync(route, parameters));

    public static Task BackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));

    public static Task DocumentAsync(string id) => GoAsync($"document?id={Uri.EscapeDataString(id)}");

    public static Task HomeAsync() => GoAsync("//main/home");
}
