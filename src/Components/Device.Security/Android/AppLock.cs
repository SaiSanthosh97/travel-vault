using Android.App;
using Android.Content;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using Android.Views;

namespace TravelVault.Device.Security;

/// <summary>
/// The app lock: fingerprint/face, or the phone's own PIN/pattern/password as fallback (no separate app PIN to
/// forget). Uses AndroidX BiometricPrompt, the system dialog, so the app never sees the fingerprint or the PIN.
/// </summary>
public static class AppLock
{
    /// <summary>True when the phone has a screen lock; without one there is nothing to verify the user with.</summary>
    public static bool IsAvailable(Context context) =>
        ((KeyguardManager)context.GetSystemService(Context.KeyguardService)!).IsDeviceSecure;

    public static Task<bool> AuthenticateAsync(FragmentActivity activity, string title, string subtitle)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            try
            {
                var prompt = new BiometricPrompt(activity, ContextCompat.GetMainExecutor(activity)!, new Callback(result));
                var info = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle(title)
                    .SetSubtitle(subtitle)
                    .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricWeak | BiometricManager.Authenticators.DeviceCredential)
                    .Build();
                prompt.Authenticate(info);
            }
            catch (Exception)
            {
                result.TrySetResult(false);
            }
        });

        return result.Task;
    }

    private sealed class Callback(TaskCompletionSource<bool> result) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult authResult) => result.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) => result.TrySetResult(false);

        // A single unrecognised finger: the system dialog stays open and lets the user try again.
        public override void OnAuthenticationFailed()
        {
        }
    }
}

/// <summary>
/// Screenshot and screen-recording protection (FLAG_SECURE): the screen shows black in screenshots, recordings,
/// screen sharing and the recent-apps preview. Turned on for identity documents, private documents, the recovery
/// code — or everywhere in Strict mode.
/// </summary>
public static class ScreenSecurity
{
    public static void SetSecure(Activity activity, bool secure) => activity.RunOnUiThread(() =>
    {
        if (secure)
        {
            activity.Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        }
        else
        {
            activity.Window?.ClearFlags(WindowManagerFlags.Secure);
        }
    });

    /// <summary>Full brightness and screen kept on while a barcode is shown at a gate scanner.</summary>
    public static void SetBarcodeMode(Activity activity, bool on) => activity.RunOnUiThread(() =>
    {
        var window = activity.Window;
        if (window is null)
        {
            return;
        }

        var attributes = window.Attributes!;
        attributes.ScreenBrightness = on ? 1f : -1f; // -1 = back to the user's setting
        window.Attributes = attributes;
        if (on)
        {
            window.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
    });
}
