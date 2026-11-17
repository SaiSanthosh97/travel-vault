using TravelVault.App.Resources.Strings;
using TravelVault.Core.Reminders;
using TravelVault.Core.Sync;
using TravelVault.Device.Reminders;

namespace TravelVault.App.Services;

/// <summary>Re-plans every reminder from the vault and hands the plan to Android (D21, D38).</summary>
public sealed class ReminderService(VaultHost host, AppSettings settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ReplanAsync()
    {
        if (!host.IsOpen)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            var s = host.Services;
            var options = ReminderSettings.Default with { AllFamilyTravel = settings.AllFamilyReminders };
            var all = new List<ScheduledReminder>();
            foreach (var family in await s.Families.ListAsync())
            {
                if (await s.Sync.IsRemovedAsync(family.Id))
                {
                    continue;
                }

                var plan = ReminderPlanner.Plan(
                    await s.Documents.ListAsync(family.Id),
                    await s.Travelers.MapAsync(family.Id),
                    s.Session.MemberId,
                    options,
                    s.Session.Now,
                    TimeZoneInfo.Local);
                all.AddRange(plan.Select(r => new ScheduledReminder(r.Key, r.FireAtUtc, r.Title, r.Body, r.DocumentId)));
            }

            TravelReminders.ScheduleAll(all);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// When sync runs: about every 15 minutes in the background (JobScheduler, off in Strict mode), every minute while
/// the app is on screen, a couple of seconds after a local change, and on pull-to-refresh.
/// </summary>
public sealed class SyncScheduler(VaultHost host, ReminderService reminders)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDispatcherTimer? _timer;

    public event Action<SyncReport>? Synced;

    public void Start()
    {
        host.NeedsSendAndReplan += () => _ = RunAsync(fullSync: false);
    }

    public void SetForeground(bool visible)
    {
        if (!visible)
        {
            _timer?.Stop();
            return;
        }

        if (_timer is null && Application.Current?.Dispatcher is { } dispatcher)
        {
            _timer = dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMinutes(1);
            _timer.Tick += (_, _) => _ = RunAsync(fullSync: true);
        }

        _timer?.Start();
        _ = RunAsync(fullSync: true);
    }

    public Task<SyncReport> SyncNowAsync() => RunAsync(fullSync: true);

    /// <summary>The background job (also runs with no screen, after the vault opens itself).</summary>
    public async Task BackgroundAsync(CancellationToken cancellationToken)
    {
        if (!await host.EnsureOpenAsync())
        {
            return;
        }

        await host.Services.Sync.SyncAllAsync(cancellationToken);
        foreach (var family in await host.Services.Families.ListAsync(cancellationToken))
        {
            await host.Services.DocumentService.PurgeOldDeletedAsync(family.Id, cancellationToken);
        }

        await reminders.ReplanAsync();
    }

    private async Task<SyncReport> RunAsync(bool fullSync)
    {
        if (!host.IsOpen || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            if (host.IsOpen && !fullSync)
            {
                await reminders.ReplanAsync();
            }

            return SyncReport.Empty;
        }

        await _gate.WaitAsync();
        try
        {
            var sync = host.Services.Sync;
            SyncReport report;
            if (fullSync)
            {
                report = await sync.SyncAllAsync();
            }
            else
            {
                foreach (var family in await host.Services.Families.ListAsync())
                {
                    await sync.FlushAsync(family.Id);
                }

                report = SyncReport.Empty;
            }

            await reminders.ReplanAsync();
            MainThread.BeginInvokeOnMainThread(() => Synced?.Invoke(report));
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Android.Util.Log.Warn("TravelVault", $"Sync failed: {ex.Message}");
            return new SyncReport(0, 0, [AppResources.Sync_Failed]);
        }
        finally
        {
            _gate.Release();
        }
    }
}
