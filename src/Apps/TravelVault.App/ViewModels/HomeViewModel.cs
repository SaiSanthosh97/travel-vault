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
/// A Home flash card (D66): one upcoming or ongoing trip, or a single ticket that isn't in a trip, with its next journey.
/// </summary>
public sealed record JourneyCard(string Id, bool IsDocument, string Name, string Dates, string Count, string Icon, string NextLabel, string NextRoute, string NextWhen, string NextDetails)
{
    /// <summary>"12–18 Dec 2026  ·  3 journeys", without separators for missing parts.</summary>
    public string Summary => string.Join("  ·  ", new[] { Dates, Count }.Where(s => s.Length > 0));
}

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
    public partial string SyncStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

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
        Suggestions.Clear();
        Cards.Clear();
        HasCards = false;
        IsEmpty = false;
        if (family is null)
        {
            return;
        }

        Removed = await s.Sync.IsRemovedAsync(family.Id);
        var travelers = await s.Travelers.MapAsync(family.Id);
        var now = s.Session.Now;
        var docs = await s.Documents.ListAsync(family.Id);

        // Flash cards (D66): upcoming and ongoing trips, and tickets that aren't in a trip, soonest first.
        Cards.Clear();
        var cards = new List<(DateTimeOffset Start, JourneyCard Card)>();
        foreach (var trip in (await s.TripService.ListAsync(family.Id)).Where(t => t.Status(now) != TripStatus.Completed && t.StartsUtc is not null))
        {
            var dates = TripStatuses.DateRange(trip.StartsUtc!.Value.LocalDateTime, (trip.EndsUtc ?? trip.StartsUtc).Value.LocalDateTime);
            cards.Add((trip.StartsUtc.Value, Card(trip.Trip.Id, false, trip.Trip.Name, dates, trip.Documents)));
        }

        foreach (var doc in docs.Where(d => d.TripId is null && d.StartsUtc is not null
                                            && d.Type is not (DocumentType.Visa or DocumentType.Passport or DocumentType.Other or DocumentType.BoardingPass)
                                            && TripStatuses.Of(d.StartsUtc, d.EndsUtc, now) != TripStatus.Completed))
        {
            cards.Add((doc.StartsUtc!.Value, Card(doc.Id, true, doc.Title, Display.When(doc.Segments.FirstOrDefault()?.Departs), [doc])));
        }

        foreach (var (_, card) in cards.OrderBy(c => c.Start))
        {
            Cards.Add(card);
        }

        HasCards = Cards.Count > 0;

        foreach (var suggestion in await s.TripService.SuggestionsAsync(family.Id))
        {
            Suggestions.Add(new SuggestionRow(suggestion, string.Format(AppResources.Home_SuggestTrip, suggestion.DocumentIds.Count, suggestion.Name)));
        }

        IsEmpty = !HasCards;
        await UpdateSyncStatusAsync(family.Id);

        // The card shows the next journey that hasn't arrived yet (or the last one, if all are under way).
        JourneyCard Card(string id, bool isDocument, string name, string dates, IReadOnlyList<TravelDocument> members)
        {
            var legs = members.SelectMany(d => d.Segments.Select(seg => (Doc: d, Seg: seg))).Where(x => x.Seg.DepartsUtc is not null).OrderBy(x => x.Seg.DepartsUtc).ToList();
            var next = legs.FirstOrDefault(x => (x.Seg.ArrivesUtc ?? x.Seg.DepartsUtc) > now);
            if (next.Seg is null && legs.Count > 0)
            {
                next = legs[^1];
            }

            var journeys = members.Count(d => d.Type is DocumentType.Flight or DocumentType.Train or DocumentType.Bus);
            var count = journeys switch
            {
                0 => "",
                1 => AppResources.Home_OneJourney,
                _ => string.Format(AppResources.Home_Journeys, journeys),
            };
            if (next.Seg is not { } seg)
            {
                return new JourneyCard(id, isDocument, name, dates, count, Display.Icon(members[0].Type), "", "", "", "");
            }

            var details = string.Join(" · ", new[]
            {
                seg.ServiceNumber,
                seg.Seat is null ? null : string.Format(AppResources.Home_Seat, seg.Seat),
                next.Doc.Pnr is null ? null : "PNR " + next.Doc.Pnr,
                Display.Travelers(next.Doc, travelers),
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new JourneyCard(id, isDocument, name, isDocument ? "" : dates, count, Display.Icon(next.Doc.Type),
                isDocument ? "" : AppResources.Home_NextJourney, Display.Route(seg), Display.When(seg.Departs), details);
        }
    }

    public ObservableCollection<JourneyCard> Cards { get; } = [];

    [ObservableProperty]
    public partial bool HasCards { get; set; }

    [RelayCommand]
    private Task OpenCardAsync(JourneyCard card) => card.IsDocument ? Nav.DocumentAsync(card.Id) : Nav.GoAsync($"trip?id={card.Id}");

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
    private Task AddAsync()
    {
        importer.PendingTripId = null; // a ticket added from Home starts on its own; the app then asks about the return
        return importer.AddAsync();
    }

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
