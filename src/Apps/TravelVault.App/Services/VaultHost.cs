using TravelVault.Core.Common;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;
using TravelVault.Device.BackgroundWork;
using TravelVault.Device.Security;
using TravelVault.Storage.Sqlite;

namespace TravelVault.App.Services;

/// <summary>
/// Owns the vault for the whole app process: create (first launch), unlock (every start — also in the background
/// for sync and reminders, which needs no user action because the Keystore key isn't auth-bound), restore, erase.
/// Screens get the services through <see cref="Services"/> once open.
/// </summary>
public sealed class VaultHost(AppSettings settings, HttpClient http) : IVaultChangeHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AndroidKeystoreVault _keyVault = new();
    private AppServices? _services;
    private CancellationTokenSource? _debounce;

    public static string Folder => Path.Combine(FileSystem.AppDataDirectory, "vault");

    private VaultBootstrapper Boot => new(Folder, _keyVault, SystemClock.Instance);

    public bool IsSetUp => Boot.IsSetUp;

    public bool IsOpen => _services is not null;

    public AppServices Services => _services ?? throw new InvalidOperationException("The vault is not open.");

    public bool HardwareBackedKey => _keyVault.IsHardwareBacked();

    /// <summary>The vault changed (on the main thread). Screens reload when they're visible.</summary>
    public event Action<VaultChange>? Changed;

    /// <summary>Messages for the user from sync ("Asha joined …").</summary>
    public event Action<string>? Notice;

    /// <summary>Local changes were made: the scheduler sends them soon. Reminders need re-planning.</summary>
    public event Action? NeedsSendAndReplan;

    public async Task<bool> EnsureOpenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_services is not null)
            {
                return true;
            }

            if (await Boot.UnlockAsync() is not { } vault)
            {
                return false;
            }

            Attach(vault);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryCode> CreateAsync(string displayName)
    {
        await _gate.WaitAsync();
        try
        {
            var (vault, code) = await Boot.CreateAsync(displayName);
            Attach(vault);
            return code;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(Stream backup, RecoveryCode code)
    {
        await _gate.WaitAsync();
        try
        {
            CloseCore();
            Attach(await Boot.RestoreAsync(backup, code));
            settings.RecoveryCodeConfirmed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Destroys the Keystore key (everything becomes unreadable), deletes all files and settings.</summary>
    public void EraseEverything()
    {
        CloseCore();
        BackgroundWork.SetEnabled(false);
        Boot.EraseEverything();
        settings.Clear();
    }

    public async Task HandleAsync(VaultChange change, CancellationToken cancellationToken = default)
    {
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke(change));

        // Many changes can arrive in one sync; re-plan once they've settled.
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cts.Token);
            NeedsSendAndReplan?.Invoke();
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void Attach(OpenedVault vault)
    {
        _services = new AppServices(vault, http, this);
        _services.Sync.Notice += (_, message) => MainThread.BeginInvokeOnMainThread(() => Notice?.Invoke(message));
        BackgroundWork.SetEnabled(!settings.StrictMode);
    }

    private void CloseCore()
    {
        _services?.Dispose();
        _services = null;
    }
}
