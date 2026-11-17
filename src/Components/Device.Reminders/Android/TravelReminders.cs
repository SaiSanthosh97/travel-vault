using Android.App;
using Android.Content;
using Android.Util;
using AndroidApp = Android.App.Application;

namespace TravelVault.Device.Reminders;

/// <summary>A reminder to schedule (built from Core.Reminders' PlannedReminder by the app).</summary>
public sealed record ScheduledReminder(string Key, DateTimeOffset FireAtUtc, string Title, string Body, string? DocumentId);

/// <summary>
/// Turns the reminder plan into Android alarms. Each reminder's key maps to its own alarm, so scheduling the whole
/// plan again replaces changed ones and cancels ones no longer planned. The notification text travels only inside
/// the alarm's intent (held by the system), never in a file; only the keys are remembered, to cancel them later.
/// </summary>
public static class TravelReminders
{
    public const string ExtraDocumentId = "travelvault.documentId";
    internal const string ChannelId = "travel-reminders";
    internal const string ExtraKey = "key";
    internal const string ExtraTitle = "title";
    internal const string ExtraBody = "body";
    private const string Preferences = "travelvault.reminders";
    private const string KeysPreference = "keys";

    private static Func<Task>? _replan;
    private static int _smallIcon;
    private static string _lockScreenText = "Travel reminder";

    private static Context AppContext => AndroidApp.Context;

    /// <summary>
    /// Call at app start-up. <paramref name="replan"/> recomputes and reschedules everything; it runs after a restart,
    /// an app update and a clock or time-zone change (Android clears alarms then).
    /// </summary>
    public static void Initialize(string channelName, string channelDescription, int smallIcon, string lockScreenText, Func<Task> replan)
    {
        _replan = replan;
        _smallIcon = smallIcon;
        _lockScreenText = lockScreenText;
        var channel = new NotificationChannel(ChannelId, channelName, NotificationImportance.High)
        {
            Description = channelDescription,
            LockscreenVisibility = NotificationVisibility.Private,
        };
        Manager.CreateNotificationChannel(channel);
    }

    /// <summary>False when Android needs the user to allow "Alarms &amp; reminders" for on-the-minute reminders.</summary>
    public static bool CanRingOnTime =>
        !OperatingSystem.IsAndroidVersionAtLeast(31) || ((AlarmManager)AppContext.GetSystemService(Context.AlarmService)!).CanScheduleExactAlarms();

    public static bool NotificationsAllowed => Manager.AreNotificationsEnabled();

    public static void OpenOnTimeSettings(Activity activity)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            activity.StartActivity(new Intent(Android.Provider.Settings.ActionRequestScheduleExactAlarm, Android.Net.Uri.Parse($"package:{activity.PackageName}")));
        }
    }

    /// <summary>Makes the scheduled alarms exactly match <paramref name="reminders"/>.</summary>
    public static void ScheduleAll(IReadOnlyList<ScheduledReminder> reminders)
    {
        var alarms = (AlarmManager)AppContext.GetSystemService(Context.AlarmService)!;
        var prefs = AppContext.GetSharedPreferences(Preferences, FileCreationMode.Private)!;
        var previous = prefs.GetStringSet(KeysPreference, null)?.ToHashSet() ?? [];
        var current = reminders.Select(r => r.Key).ToHashSet();

        foreach (var gone in previous.Except(current))
        {
            alarms.Cancel(Pending(gone, null));
        }

        foreach (var reminder in reminders)
        {
            var trigger = reminder.FireAtUtc.ToUnixTimeMilliseconds();
            var intent = Pending(reminder.Key, reminder);
            if (CanRingOnTime)
            {
                alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, intent);
            }
            else
            {
                alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, intent);
            }
        }

        prefs.Edit()!.PutStringSet(KeysPreference, current)!.Apply();
        Log.Info("TravelVault", $"Reminders scheduled: {reminders.Count}{(CanRingOnTime ? "" : " (may be a few minutes late)")}");
    }

    internal static void Show(string key, string title, string body, string? documentId)
    {
        var context = AppContext;
        var launch = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!;
        launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        if (documentId is not null)
        {
            launch.PutExtra(ExtraDocumentId, documentId);
        }

        var id = IdFor(key);
        var open = PendingIntent.GetActivity(context, id, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // On the lock screen only "Travel reminder" is shown; the details appear after unlocking.
        var publicVersion = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(_smallIcon)
            .SetContentTitle(_lockScreenText)
            .Build();

        var notification = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(_smallIcon)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new Notification.BigTextStyle().BigText(body))
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .SetVisibility(NotificationVisibility.Private)
            .SetPublicVersion(publicVersion)
            .SetCategory(Notification.CategoryReminder)
            .Build();
        Manager.Notify(id, notification);
    }

    internal static async Task ReplanAsync()
    {
        try
        {
            if (_replan is not null)
            {
                await _replan();
            }
        }
        catch (Exception ex)
        {
            Log.Error("TravelVault", $"Re-planning reminders failed: {ex}");
        }
    }

    private static PendingIntent Pending(string key, ScheduledReminder? reminder)
    {
        var intent = new Intent(AppContext, typeof(ReminderAlarmReceiver)).SetAction("travelvault.reminder." + key);
        if (reminder is not null)
        {
            intent.PutExtra(ExtraKey, reminder.Key).PutExtra(ExtraTitle, reminder.Title).PutExtra(ExtraBody, reminder.Body);
            if (reminder.DocumentId is not null)
            {
                intent.PutExtra(ExtraDocumentId, reminder.DocumentId);
            }
        }

        return PendingIntent.GetBroadcast(AppContext, IdFor(key), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static NotificationManager Manager => (NotificationManager)AppContext.GetSystemService(Context.NotificationService)!;

    /// <summary>Stable notification/alarm id from the key (ids 500000–599999, apart from other notifications).</summary>
    internal static int IdFor(string key)
    {
        var hash = 17;
        foreach (var c in key)
        {
            hash = unchecked((hash * 31) + c);
        }

        return 500000 + (int)((uint)hash % 100000);
    }
}

[BroadcastReceiver(Name = "travelvault.reminders.ReminderAlarmReceiver", Exported = false)]
public sealed class ReminderAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var key = intent?.GetStringExtra(TravelReminders.ExtraKey);
        var title = intent?.GetStringExtra(TravelReminders.ExtraTitle);
        if (key is null || title is null)
        {
            return;
        }

        TravelReminders.Show(key, title, intent!.GetStringExtra(TravelReminders.ExtraBody) ?? "", intent.GetStringExtra(TravelReminders.ExtraDocumentId));
    }
}

/// <summary>Restores reminders after a restart, an app update or a clock / time-zone change.</summary>
[BroadcastReceiver(Name = "travelvault.reminders.RescheduleReceiver", Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, Intent.ActionTimeChanged, Intent.ActionTimezoneChanged])]
public sealed class RescheduleReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        // Exported only so the system can deliver these protected broadcasts; other apps cannot send them.
        if (intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced or Intent.ActionTimeChanged or Intent.ActionTimezoneChanged))
        {
            return;
        }

        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                await TravelReminders.ReplanAsync();
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}
