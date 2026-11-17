using TravelVault.App.Resources.Strings;
using TravelVault.Device.Security;

namespace TravelVault.App.Services;

/// <summary>
/// The app lock (D10): fingerprint/face or the phone's PIN when the app starts and after it has been in the
/// background longer than the chosen time (immediately in Strict mode). If the phone has no screen lock at all,
/// the app can't verify anyone and says so in Settings.
/// </summary>
public sealed class LockService(AppSettings settings)
{
    private DateTimeOffset? _leftAt;
    private bool _unlocked;

    public bool PhoneHasScreenLock => AppLock.IsAvailable(Platform.AppContext);

    public bool IsEnabled => settings.AppLock && PhoneHasScreenLock;

    /// <summary>True when the lock screen must be shown before anything else.</summary>
    public bool NeedsUnlock
    {
        get
        {
            if (!IsEnabled)
            {
                return false;
            }

            if (!_unlocked)
            {
                return true;
            }

            var after = settings.StrictMode ? TimeSpan.Zero : settings.LockAfter;
            return _leftAt is { } left && DateTimeOffset.UtcNow - left >= after;
        }
    }

    public void Left() => _leftAt = DateTimeOffset.UtcNow;

    public void Returned()
    {
        if (NeedsUnlock)
        {
            _unlocked = false;
        }

        _leftAt = null;
    }

    public async Task<bool> UnlockAsync()
    {
        if (!IsEnabled)
        {
            _unlocked = true;
            return true;
        }

        if (Activity.Current is not { } activity)
        {
            return false;
        }

        _unlocked = await AppLock.AuthenticateAsync(activity, AppResources.Lock_Title, AppResources.Lock_Subtitle);
        _leftAt = null;
        return _unlocked;
    }

    /// <summary>For sensitive actions (showing the recovery code, erasing): ask again even if unlocked.</summary>
    public async Task<bool> ConfirmIdentityAsync() =>
        !PhoneHasScreenLock || (Activity.Current is { } activity && await AppLock.AuthenticateAsync(activity, AppResources.Lock_ConfirmTitle, AppResources.Lock_Subtitle));
}
