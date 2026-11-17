using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.Views;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Trips;

namespace TravelVault.App.ViewModels;

public sealed record SuggestionRow(TripSuggestion Suggestion, string Text);

/// <summary>
/// Home: the next thing to use (FR-R4) with one tap to the ticket, trip suggestions to confirm (FR-R2), upcoming
/// documents, adding documents (camera, photo, file, pasted text), and the sync status.
/// </summary>
public sealed partial class HomeViewModel(VaultHost host, AppSettings settings, ImportCoordinator importer, SyncScheduler scheduler, Dialogs dialogs)
    : ObservableObject, ILoads
{
    private IReadOnlyList<Family> _families = [];

    [ObservableProperty]
    public partial string FamilyName { get; set; } = "";

    [ObservableProperty]
    public partial bool HasFamily { get; set; }

    [ObservableProperty]
    public partial bool NoFamily { get; set; }

    [ObservableProperty]
    public partial bool ManyFamilies { get; set; }

    [ObservableProperty]
    public partial bool Removed { get; set; }

    [ObservableProperty]
    public partial bool HasNextUp { get; set; }

    [ObservableProperty]
    public partial string NextUpTitle { get; set; } = "";

    [ObservableProperty]
    public partial string NextUpWhen { get; set; } = "";

    [ObservableProperty]
    public partial string NextUpDetails { get; set; } = "";

    [ObservableProperty]
    public partial string NextUpIcon { get; set; } = "";

    [ObservableProperty]
    public partial bool NextUpHasBoardingPass { get; set; }

    [ObservableProperty]
    public partial string SyncStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    private string? _nextUpDocumentId;
    private string? _nextUpBoardingPassId;

    public ObservableCollection<DocumentRow> Upcoming { get; } = [];

    public ObservableCollection<SuggestionRow> Suggestions { get; } = [];

    public async Task LoadAsync()
    {
        var s = host.Services;
        _families = await s.Families.ListAsync();
        var familyId = await importer.CurrentFamilyAsync();
        var family = _families.FirstOrDefault(f => f.Id == familyId);
        HasFamily = family is not null;
        NoFamily = family is null;
        ManyFamilies = _families.Count > 1;
        FamilyName = family?.Name ?? "";
        Upcoming.Clear();
        Suggestions.Clear();
        HasNextUp = false;
        if (family is null)
        {
            return;
        }

        Removed = await s.Sync.IsRemovedAsync(family.Id);
        var travelers = await s.Travelers.MapAsync(family.Id);
        var now = s.Session.Now;
        var docs = await s.Documents.ListAsync(family.Id);

        if (await s.DocumentService.NextUpAsync(family.Id) is { } next)
        {
            HasNextUp = true;
            _nextUpDocumentId = next.Document.Id;
            NextUpIcon = Display.Icon(next.Document.Type);
            NextUpTitle = Display.Route(next.Segment);
            NextUpWhen = Display.When(next.Segment.Departs);
            NextUpDetails = string.Join(" · ", new[]
            {
                next.Segment.ServiceNumber,
                next.Document.Pnr is null ? null : "PNR " + next.Document.Pnr,
                next.Segment.Seat is null ? null : string.Format(AppResources.Home_Seat, next.Segment.Seat),
                Display.Travelers(next.Document, travelers),
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var pass = BoardingPassLinker.LinkedTo(next.Document, docs).FirstOrDefault(p => p.BoardingPass?.SegmentId == next.Segment.Id);
            _nextUpBoardingPassId = pass?.Id;
            NextUpHasBoardingPass = pass is not null;
        }

        foreach (var suggestion in await s.TripService.SuggestionsAsync(family.Id))
        {
            Suggestions.Add(new SuggestionRow(suggestion, string.Format(AppResources.Home_SuggestTrip, suggestion.DocumentIds.Count, suggestion.Name)));
        }

        foreach (var doc in docs.Where(d => d.EndsUtc is { } end && end > now.AddHours(-6) && d.Type != DocumentType.BoardingPass).Take(30))
        {
            Upcoming.Add(Display.Row(doc, travelers, now));
        }

        IsEmpty = docs.Count == 0;
        await UpdateSyncStatusAsync(family.Id);
    }

    private async Task UpdateSyncStatusAsync(string familyId)
    {
        var s = host.Services;
        var pending = await s.Sync.PendingCountAsync(familyId);
        var last = await s.Sync.LastSyncAsync(familyId);
        SyncStatus = pending > 0
            ? string.Format(AppResources.Sync_Waiting, pending)
            : last is { } t ? string.Format(AppResources.Sync_Last, Display.Ago(s.Session.Now - t)) : AppResources.Sync_Never;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var report = await scheduler.SyncNowAsync();
            if (report.Errors.Count > 0)
            {
                Dialogs.Toast(AppResources.Sync_Failed);
            }

            await LoadAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task SwitchFamilyAsync()
    {
        var choice = await dialogs.ChooseAsync(AppResources.Home_ChooseFamily, [.. _families.Select(f => f.Name)]);
        if (_families.FirstOrDefault(f => f.Name == choice) is { } family)
        {
            settings.CurrentFamilyId = family.Id;
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task CreateFamilyAsync()
    {
        var name = await dialogs.PromptAsync(AppResources.Family_CreateTitle, AppResources.Family_CreatePrompt, string.Format(AppResources.Family_DefaultName, host.Services.Session.DisplayName), maxLength: 60);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var family = await host.Services.Sync.CreateFamilyAsync(name.Trim());
        settings.CurrentFamilyId = family.Id;
        await LoadAsync();
    }

    [RelayCommand]
    private Task JoinFamilyAsync() => Nav.GoAsync("join");

    [RelayCommand]
    private async Task AddAsync()
    {
        var choice = await dialogs.ChooseAsync(AppResources.Add_Title, AppResources.Add_Camera, AppResources.Add_Photo, AppResources.Add_File, AppResources.Add_Paste);
        try
        {
            if (choice == AppResources.Add_Camera)
            {
                if (await MediaPicker.Default.CapturePhotoAsync() is { } photo)
                {
                    await ImportFileAsync(photo);
                }
            }
            else if (choice == AppResources.Add_Photo)
            {
                foreach (var picked in await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 10 }) ?? [])
                {
                    await ImportFileAsync(picked);
                }
            }
            else if (choice == AppResources.Add_File)
            {
                var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
                {
                    PickerTitle = AppResources.Add_File,
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.Android] = ["application/pdf", "image/*", "text/plain"] }),
                });
                foreach (var file in files ?? [])
                {
                    if (file is not null)
                    {
                        await ImportFileAsync(file);
                    }
                }
            }
            else if (choice == AppResources.Add_Paste)
            {
                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    await dialogs.AlertAsync(AppResources.Add_Title, AppResources.Add_ClipboardEmpty);
                    return;
                }

                await importer.ImportAsync(System.Text.Encoding.UTF8.GetBytes(text), null);
            }
        }
        catch (PermissionException)
        {
            await dialogs.AlertAsync(AppResources.Add_Title, AppResources.Add_NoPermission);
        }
    }

    private async Task ImportFileAsync(FileResult file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        await importer.ImportAsync(buffer.ToArray(), file.FileName);
    }

    [RelayCommand]
    private Task OpenNextUpAsync() => _nextUpDocumentId is null ? Task.CompletedTask : Nav.DocumentAsync(_nextUpDocumentId);

    [RelayCommand]
    private Task OpenBoardingPassAsync() => _nextUpBoardingPassId is null ? Task.CompletedTask : Nav.GoAsync($"viewer?id={_nextUpBoardingPassId}&barcode=1");

    [RelayCommand]
    private Task OpenAsync(DocumentRow row) => Nav.DocumentAsync(row.Id);

    [RelayCommand]
    private async Task ConfirmSuggestionAsync(SuggestionRow row)
    {
        var name = await dialogs.PromptAsync(AppResources.Trip_NameTitle, AppResources.Trip_NamePrompt, row.Suggestion.Name, maxLength: 60);
        if (name is null || await importer.CurrentFamilyAsync() is not { } familyId)
        {
            return;
        }

        await host.Services.TripService.ConfirmAsync(familyId, row.Suggestion, name);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DismissSuggestionAsync(SuggestionRow row)
    {
        if (await importer.CurrentFamilyAsync() is { } familyId)
        {
            await host.Services.TripService.DismissAsync(familyId, row.Suggestion);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteRemovedFamilyAsync()
    {
        if (await importer.CurrentFamilyAsync() is { } familyId
            && await dialogs.ConfirmAsync(AppResources.Family_RemovedTitle, AppResources.Family_DeleteDataConfirm, AppResources.Common_Delete))
        {
            await host.Services.Sync.DeleteFamilyDataAsync(familyId);
            settings.CurrentFamilyId = null;
            await LoadAsync();
        }
    }
}
