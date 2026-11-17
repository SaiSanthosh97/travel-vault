namespace TravelVault.App.Services;

/// <summary>
/// UI preferences (Android SharedPreferences). Only non-sensitive values live here — which family is open, lock
/// timing, switches. Everything about documents, people and keys is in the encrypted vault.
/// </summary>
public sealed class AppSettings
{
    private static IPreferences P => Preferences.Default;

    public string? CurrentFamilyId
    {
        get => P.Get<string?>("family.current", null);
        set => P.Set("family.current", value);
    }

    public bool AppLock
    {
        get => P.Get("lock.enabled", true);
        set => P.Set("lock.enabled", value);
    }

    /// <summary>Lock again after the app has been in the background this long.</summary>
    public TimeSpan LockAfter
    {
        get => TimeSpan.FromSeconds(P.Get("lock.afterSeconds", 60));
        set => P.Set("lock.afterSeconds", (int)value.TotalSeconds);
    }

    /// <summary>
    /// Strict mode (approved design point 4): no background sync, lock every time the app is left, screenshots
    /// blocked on every screen.
    /// </summary>
    public bool StrictMode
    {
        get => P.Get("security.strict", false);
        set => P.Set("security.strict", value);
    }

    /// <summary>Reminders for the whole family's travel (true) or only my own (false).</summary>
    public bool AllFamilyReminders
    {
        get => P.Get("reminders.allFamily", true);
        set => P.Set("reminders.allFamily", value);
    }

    public bool RecoveryCodeConfirmed
    {
        get => P.Get("setup.recoveryConfirmed", false);
        set => P.Set("setup.recoveryConfirmed", value);
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => P.Get("update.lastCheck", 0L) is var ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        set => P.Set("update.lastCheck", value?.ToUnixTimeMilliseconds() ?? 0L);
    }

    public void Clear() => P.Clear();
}
