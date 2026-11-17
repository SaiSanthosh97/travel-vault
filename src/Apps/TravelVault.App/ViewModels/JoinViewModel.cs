using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.Core.Families;
using TravelVault.Core.Sync;

namespace TravelVault.App.ViewModels;

/// <summary>
/// Joining a family (J2): scan the admin's QR code (in person, joins at once), or paste the invite message (remote:
/// read the safety code to the admin on a call; joined after their approval).
/// </summary>
public sealed partial class JoinViewModel(VaultHost host, AppSettings settings, Dialogs dialogs) : ObservableObject
{
    private RemoteInvite? _pending;

    [ObservableProperty]
    public partial bool Waiting { get; set; }

    [ObservableProperty]
    public partial string SafetyCode { get; set; } = "";

    [ObservableProperty]
    public partial string WaitingText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            if (await MediaPicker.Default.CapturePhotoAsync() is not { } photo)
            {
                return;
            }

            await using var stream = await photo.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var codes = await host.Services.BarcodeScanner.ScanAsync(buffer.ToArray());
            var text = codes.Select(c => c.Value).FirstOrDefault(v => v.StartsWith("tv1:", StringComparison.Ordinal));
            if (text is null)
            {
                await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_NoQr);
                return;
            }

            await UseAsync(text);
        }
        catch (PermissionException)
        {
            await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Add_NoPermission);
        }
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = await dialogs.PromptAsync(AppResources.Join_Title, AppResources.Join_PastePrompt, maxLength: 4000);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            await UseAsync(text);
        }
    }

    private async Task UseAsync(string text)
    {
        IsBusy = true;
        try
        {
            switch (InviteCodec.Decode(text, DateTimeOffset.UtcNow))
            {
                case InPersonInvite inPerson:
                    var family = await host.Services.Sync.JoinInPersonAsync(inPerson);
                    settings.CurrentFamilyId = family.Id;
                    await host.Services.Sync.SyncAllAsync();
                    Dialogs.Toast(string.Format(AppResources.Join_Joining, family.Name));
                    await Nav.HomeAsync();
                    break;
                case RemoteInvite remote:
                    _pending = remote;
                    SafetyCode = await host.Services.Sync.RequestRemoteJoinAsync(remote);
                    WaitingText = string.Format(AppResources.Join_Waiting, remote.FamilyName);
                    Waiting = true;
                    break;
                default:
                    await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_Invalid);
                    break;
            }
        }
        catch (HttpRequestException)
        {
            await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_Offline);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_pending is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            switch (await host.Services.Sync.CheckJoinAsync(_pending.InviteId))
            {
                case JoinOutcome.Joined:
                    settings.CurrentFamilyId = _pending.FamilyId;
                    await host.Services.Sync.SyncAllAsync();
                    Dialogs.Toast(string.Format(AppResources.Join_Joined, _pending.FamilyName));
                    await Nav.HomeAsync();
                    break;
                case JoinOutcome.Rejected:
                    Waiting = false;
                    await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_Rejected);
                    break;
                case JoinOutcome.Expired:
                    Waiting = false;
                    await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_Expired);
                    break;
                default:
                    Dialogs.Toast(AppResources.Join_StillWaiting);
                    break;
            }
        }
        catch (HttpRequestException)
        {
            await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_Offline);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
