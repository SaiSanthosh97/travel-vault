using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.Views;

namespace TravelVault.App.ViewModels;

public sealed record TravelerRow(string Id, string Name, string Details, bool IsMe);

/// <summary>Traveler profiles (FR-T1): names, the other ways they're printed on tickets, and who is "me".</summary>
public sealed partial class TravelersViewModel(VaultHost host, ImportCoordinator importer, Dialogs dialogs) : ObservableObject, ILoads
{
    public ObservableCollection<TravelerRow> Travelers { get; } = [];

    public async Task LoadAsync()
    {
        Travelers.Clear();
        if (await importer.CurrentFamilyAsync() is not { } familyId)
        {
            return;
        }

        var s = host.Services;
        var family = await s.Families.GetAsync(familyId);
        foreach (var t in await s.Travelers.ListAsync(familyId))
        {
            var member = t.LinkedMemberId is { } m ? family?.Find(m)?.DisplayName : null;
            var details = string.Join(" · ", new[]
            {
                t.LinkedMemberId == s.Session.MemberId ? AppResources.Travelers_You : member is null ? null : string.Format(AppResources.Travelers_Phone, member),
                t.Aliases.Count == 0 ? null : string.Format(AppResources.Travelers_AlsoPrinted, string.Join(", ", t.Aliases)),
            }.Where(x => x is not null));
            Travelers.Add(new TravelerRow(t.Id, t.DisplayName, details, t.LinkedMemberId == s.Session.MemberId));
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (await importer.CurrentFamilyAsync() is not { } familyId)
        {
            return;
        }

        var name = await dialogs.PromptAsync(AppResources.Travelers_Add, AppResources.Travelers_NamePrompt, maxLength: 60);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await host.Services.Travelers.CreateAsync(familyId, name.Trim());
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task EditAsync(TravelerRow row)
    {
        var choice = await dialogs.ChooseAsync(row.Name, AppResources.Travelers_Rename, AppResources.Travelers_AddAlias, AppResources.Travelers_RemoveAlias, AppResources.Travelers_ThisIsMe, AppResources.Common_Delete);
        var s = host.Services;
        if (choice == AppResources.Travelers_Rename)
        {
            var name = await dialogs.PromptAsync(AppResources.Travelers_Rename, AppResources.Travelers_NamePrompt, row.Name, maxLength: 60);
            if (!string.IsNullOrWhiteSpace(name))
            {
                await s.Travelers.RenameAsync(row.Id, name);
            }
        }
        else if (choice == AppResources.Travelers_AddAlias)
        {
            var alias = await dialogs.PromptAsync(AppResources.Travelers_AddAlias, AppResources.Travelers_AliasPrompt, maxLength: 60);
            if (!string.IsNullOrWhiteSpace(alias) && await s.TravelerRepository.GetAsync(row.Id) is { } traveler)
            {
                await s.Travelers.LearnAliasAsync(traveler, alias);
            }
        }
        else if (choice == AppResources.Travelers_RemoveAlias && await s.TravelerRepository.GetAsync(row.Id) is { Aliases.Count: > 0 } withAliases)
        {
            if (await dialogs.ChooseAsync(AppResources.Travelers_RemoveAlias, [.. withAliases.Aliases]) is { } alias)
            {
                await s.Travelers.RemoveAliasAsync(row.Id, alias);
            }
        }
        else if (choice == AppResources.Travelers_ThisIsMe)
        {
            await s.Travelers.LinkMemberAsync(row.Id, s.Session.MemberId);
        }
        else if (choice == AppResources.Common_Delete && await dialogs.ConfirmAsync(AppResources.Common_Delete, string.Format(AppResources.Travelers_DeleteText, row.Name), AppResources.Common_Delete))
        {
            await s.Travelers.DeleteAsync(row.Id);
        }

        await LoadAsync();
    }
}

/// <summary>Deleted documents are kept 30 days (FR-D7) and can be restored.</summary>
public sealed partial class RecentlyDeletedViewModel(VaultHost host, ImportCoordinator importer) : ObservableObject, ILoads
{
    public ObservableCollection<DocumentRow> Documents { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public async Task LoadAsync()
    {
        Documents.Clear();
        if (await importer.CurrentFamilyAsync() is { } familyId)
        {
            var s = host.Services;
            var travelers = await s.Travelers.MapAsync(familyId);
            foreach (var doc in await s.DocumentService.RecentlyDeletedAsync(familyId))
            {
                Documents.Add(Display.Row(doc, travelers, s.Session.Now));
            }
        }

        IsEmpty = Documents.Count == 0;
    }

    [RelayCommand]
    private async Task RestoreAsync(DocumentRow row)
    {
        await host.Services.DocumentService.RestoreAsync(row.Id);
        Dialogs.Toast(string.Format(AppResources.Deleted_Restored, row.Title));
        await LoadAsync();
    }
}
