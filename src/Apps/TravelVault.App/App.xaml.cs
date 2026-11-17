using TravelVault.App.Services;
using TravelVault.App.Views;

namespace TravelVault.App;

/// <summary>
/// App lifecycle: locks when the app comes back after the chosen time, syncs every minute while visible, and
/// imports anything shared with the app once it is unlocked.
/// </summary>
public partial class App : Application
{
    private readonly VaultHost _host;
    private readonly LockService _lock;
    private readonly SyncScheduler _scheduler;
    private readonly ImportCoordinator _importer;
    private bool _lockShown;

    public App(VaultHost host, LockService lockService, SyncScheduler scheduler, ImportCoordinator importer)
    {
        InitializeComponent();
        _host = host;
        _lock = lockService;
        _scheduler = scheduler;
        _importer = importer;
        Inbox.Arrived += () => MainThread.BeginInvokeOnMainThread(() => _ = AfterUnlockAsync());
        host.Notice += Dialogs.Toast;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
        window.Stopped += (_, _) =>
        {
            _lock.Left();
            _scheduler.SetForeground(false);
        };
        window.Resumed += (_, _) => _ = ResumedAsync();
        return window;
    }

    private async Task ResumedAsync()
    {
        _lock.Returned();
        if (_host.IsOpen && _lock.NeedsUnlock)
        {
            await ShowLockAsync();
        }
        else
        {
            await AfterUnlockAsync();
        }
    }

    /// <summary>Shows the lock screen over whatever is open, so the user returns to the same place after unlocking.</summary>
    public async Task ShowLockAsync()
    {
        if (_lockShown || Shell.Current is null)
        {
            return;
        }

        _lockShown = true;
        var page = new LockPage(_lock, async () =>
        {
            _lockShown = false;
            await Shell.Current.Navigation.PopModalAsync(animated: false);
            await AfterUnlockAsync();
        });
        await Shell.Current.Navigation.PushModalAsync(page, animated: false);
    }

    /// <summary>Everything that should happen once the vault is open and unlocked.</summary>
    public async Task AfterUnlockAsync()
    {
        if (!_host.IsOpen || _lock.NeedsUnlock || _lockShown || Shell.Current?.CurrentState.Location.OriginalString.Contains("main", StringComparison.Ordinal) != true)
        {
            return;
        }

        _scheduler.SetForeground(true);
        await _importer.ProcessInboxAsync();
    }
}
