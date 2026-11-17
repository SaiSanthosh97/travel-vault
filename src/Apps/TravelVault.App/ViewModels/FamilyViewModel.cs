using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.Views;
using TravelVault.Core.Families;
using TravelVault.Core.Sync;

namespace TravelVault.App.ViewModels;

public sealed record MemberRow(string MemberId, string Name, string Detail, bool CanRemove);

public sealed record PendingJoinRow(string MemberId, string Name, string SafetyCode);

/// <summary>
/// The family (J2, J13): who's in it and when each phone was last seen, inviting (QR in person, or a message with
/// approval + safety code), approving join requests, removing members, leaving, other families, sync status.
/// </summary>
public sealed partial class FamilyViewModel(VaultHost host, AppSettings settings, ImportCoordinator importer, SyncScheduler scheduler, Dialogs dialogs)
    : ObservableObject, ILoads
{
    private Family? _family;

    [ObservableProperty]
    public partial string FamilyName { get; set; } = "";

    [ObservableProperty]
    public partial bool HasFamily { get; set; }

    [ObservableProperty]
    public partial bool IsAdmin { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? InviteQr { get; set; }

    [ObservableProperty]
    public partial bool ShowQr { get; set; }

    [ObservableProperty]
    public partial string QrExpiry { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<MemberRow> Members { get; } = [];

    public ObservableCollection<PendingJoinRow> Pending { get; } = [];

    public async Task LoadAsync()
    {
        var s = host.Services;
        Members.Clear();
        Pending.Clear();
        var id = await importer.CurrentFamilyAsync();
        _family = id is null ? null : await s.Families.GetAsync(id);
        HasFamily = _family is not null;
        if (_family is null)
        {
            return;
        }

        var me = s.Session.MemberId;
        FamilyName = _family.Name;
        IsAdmin = _family.IsAdmin(me);
        foreach (var member in _family.ActiveMembers.OrderByDescending(m => m.MemberId == me).ThenBy(m => m.DisplayName))
        {
            var role = member.Role == MemberRole.Admin ? AppResources.Family_Admin : AppResources.Family_Member;
            string detail;
            if (member.MemberId == me)
            {
                detail = $"{role} · {AppResources.Family_ThisPhone}";
            }
            else
            {
                var seen = await s.Sync.LastSeenAsync(_family.Id, member.MemberId);
                detail = seen is { } t ? $"{role} · {string.Format(AppResources.Family_LastSeen, Display.Ago(s.Session.Now - t))}" : role;
            }

            Members.Add(new MemberRow(member.MemberId, member.DisplayName, detail, IsAdmin && member.MemberId != me));
        }

        foreach (var join in await s.Sync.PendingJoinsAsync(_family.Id))
        {
            Pending.Add(new PendingJoinRow(join.Member.MemberId, join.Member.DisplayName, join.SafetyCode));
        }

        var last = await s.Sync.LastSyncAsync(_family.Id);
        Status = !FamilySync.Admitted(_family) ? AppResources.Family_WaitingAdmission
            : last is { } l ? string.Format(AppResources.Sync_Last, Display.Ago(s.Session.Now - l))
            : AppResources.Sync_Never;
    }

    [RelayCommand]
    private async Task InviteInPersonAsync()
    {
        if (_family is null)
        {
            return;
        }

        var invite = await host.Services.Sync.CreateInPersonInviteAsync(_family.Id);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(InviteCodec.Encode(invite), QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);
        InviteQr = ImageSource.FromStream(() => new MemoryStream(png));
        QrExpiry = string.Format(AppResources.Family_QrExpires, invite.ExpiresAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture));
        ShowQr = true;
        Services.Activity.SetSecure(true, settings); // the QR holds the family key: no screenshots
    }

    [RelayCommand]
    private void HideQr()
    {
        ShowQr = false;
        InviteQr = null;
        Services.Activity.SetSecure(false, settings);
    }

    [RelayCommand]
    private async Task InviteByMessageAsync()
    {
        if (_family is null)
        {
            return;
        }

        var invite = await host.Services.Sync.CreateRemoteInviteAsync(_family.Id);
        await Share.Default.RequestAsync(new ShareTextRequest(InviteCodec.ShareText(invite), AppResources.Family_InviteTitle));
        await dialogs.AlertAsync(AppResources.Family_InviteTitle, AppResources.Family_InviteSent);
    }

    [RelayCommand]
    private async Task ApproveAsync(PendingJoinRow row)
    {
        if (_family is null || !await dialogs.ConfirmAsync(AppResources.Family_ApproveTitle, string.Format(AppResources.Family_ApproveText, row.Name, row.SafetyCode), AppResources.Family_Approve))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await host.Services.Sync.ApproveJoinAsync(_family.Id, row.MemberId);
            Dialogs.Toast(string.Format(AppResources.Family_Approved, row.Name));
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task RejectAsync(PendingJoinRow row)
    {
        if (_family is not null)
        {
            await host.Services.Sync.RejectJoinAsync(_family.Id, row.MemberId);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(MemberRow row)
    {
        if (_family is null || !await dialogs.ConfirmAsync(AppResources.Family_RemoveTitle, string.Format(AppResources.Family_RemoveText, row.Name), AppResources.Family_Remove))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await host.Services.Sync.RemoveMemberAsync(_family.Id, row.MemberId);
        }
        catch (FamilyRuleException ex)
        {
            await dialogs.AlertAsync(AppResources.Family_RemoveTitle, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task MakeAdminAsync(MemberRow row)
    {
        if (_family is null || !await dialogs.ConfirmAsync(AppResources.Family_MakeAdmin, string.Format(AppResources.Family_MakeAdminText, row.Name), AppResources.Common_OK))
        {
            return;
        }

        _family.SetRole(row.MemberId, MemberRole.Admin, host.Services.Session.MemberId);
        host.Services.Session.Stamp(_family);
        await host.Services.Families.SaveAsync(_family);
        await host.Services.Events.PublishAsync(new Core.Vault.VaultChange(_family.Id, Core.Vault.RecordType.Family, _family.Id));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (_family is null || !await dialogs.ConfirmAsync(AppResources.Family_LeaveTitle, string.Format(AppResources.Family_LeaveText, _family.Name), AppResources.Family_Leave))
        {
            return;
        }

        try
        {
            await host.Services.Sync.LeaveFamilyAsync(_family.Id);
            settings.CurrentFamilyId = null;
            await LoadAsync();
        }
        catch (FamilyRuleException ex)
        {
            await dialogs.AlertAsync(AppResources.Family_LeaveTitle, ex.Message);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (_family is null)
        {
            return;
        }

        var name = await dialogs.PromptAsync(AppResources.Family_Rename, AppResources.Family_CreatePrompt, _family.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _family.Rename(name, host.Services.Session.MemberId);
        host.Services.Session.Stamp(_family);
        await host.Services.Families.SaveAsync(_family);
        await host.Services.Events.PublishAsync(new Core.Vault.VaultChange(_family.Id, Core.Vault.RecordType.Family, _family.Id));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CreateAnotherAsync()
    {
        var name = await dialogs.PromptAsync(AppResources.Family_CreateTitle, AppResources.Family_CreatePrompt, maxLength: 60);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var family = await host.Services.Sync.CreateFamilyAsync(name.Trim());
            settings.CurrentFamilyId = family.Id;
            await LoadAsync();
        }
    }

    [RelayCommand]
    private Task JoinAsync() => Nav.GoAsync("join");

    [RelayCommand]
    private Task TravelersAsync() => Nav.GoAsync("travelers");

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        IsBusy = true;
        try
        {
            var report = await scheduler.SyncNowAsync();
            Dialogs.Toast(report.Errors.Count > 0 ? AppResources.Sync_Failed : string.Format(AppResources.Sync_Done, report.Received));
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }
}
