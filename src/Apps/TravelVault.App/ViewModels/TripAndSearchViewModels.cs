using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.Views;
using TravelVault.Core.Documents;
using TravelVault.Core.Trips;

namespace TravelVault.App.ViewModels;

/// <param name="IsDocument">A single document that isn't part of any trip (opens the document instead of a trip).</param>
public sealed record TripRow(string Id, string Name, string Dates, string Details, bool IsDocument = false);

/// <summary>
/// Trips by status (FR-R3): Upcoming, Ongoing, Completed. Documents that aren't in a trip are listed too, as one-item
/// journeys (C-018): a single flight or hotel is still an upcoming or completed journey for the person looking.
/// </summary>
public sealed partial class TripsViewModel(VaultHost host, ImportCoordinator importer) : ObservableObject, ILoads
{
    [ObservableProperty]
    public partial TripStatus Status { get; set; } = TripStatus.Upcoming;

    [ObservableProperty]
    public partial string StatusText { get; set; } = AppResources.Trips_Upcoming;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public ObservableCollection<TripRow> Trips { get; } = [];

    public async Task LoadAsync()
    {
        Trips.Clear();
        StatusText = Status switch
        {
            TripStatus.Upcoming => AppResources.Trips_Upcoming,
            TripStatus.Ongoing => AppResources.Trips_Ongoing,
            _ => AppResources.Trips_Completed,
        };
        if (await importer.CurrentFamilyAsync() is not { } familyId)
        {
            IsEmpty = true;
            return;
        }

        var s = host.Services;
        var travelers = await s.Travelers.MapAsync(familyId);
        var now = s.Session.Now;
        var rows = new List<(DateTimeOffset? Start, TripRow Row)>();
        foreach (var trip in (await s.TripService.ByStatusAsync(familyId))[Status])
        {
            var names = string.Join(", ", trip.TravelerIds.Select(id => travelers.TryGetValue(id, out var t) ? t.DisplayName : null).Where(n => n is not null));
            var dates = trip.StartsUtc is { } a && trip.EndsUtc is { } b ? TripStatuses.DateRange(a.LocalDateTime, b.LocalDateTime) : "";
            rows.Add((trip.StartsUtc, new TripRow(trip.Trip.Id, trip.Trip.Name, dates, string.Format(AppResources.Trips_Documents, trip.Documents.Count) + (names.Length > 0 ? " · " + names : ""))));
        }

        // Journeys that aren't in a trip: transport, stays and events with dates (boarding passes show with their ticket).
        var loose = (await s.Documents.ListAsync(familyId)).Where(d =>
            d.TripId is null
            && d.StartsUtc is not null
            && d.Type is not (DocumentType.Visa or DocumentType.Passport or DocumentType.Other)
            && (d.Type != DocumentType.BoardingPass || d.BoardingPass is null)
            && TripStatuses.Of(d.StartsUtc, d.EndsUtc, now) == Status);
        foreach (var doc in loose)
        {
            var row = Display.Row(doc, travelers, now);
            rows.Add((doc.StartsUtc, new TripRow(doc.Id, $"{row.Icon} {doc.Title}", row.When, row.Subtitle, IsDocument: true)));
        }

        var ordered = Status == TripStatus.Completed ? rows.OrderByDescending(r => r.Start) : rows.OrderBy(r => r.Start);
        foreach (var (_, row) in ordered)
        {
            Trips.Add(row);
        }

        IsEmpty = Trips.Count == 0;
    }

    [RelayCommand]
    private async Task ShowAsync(string status)
    {
        Status = Enum.Parse<TripStatus>(status);
        await LoadAsync();
    }

    [RelayCommand]
    private Task OpenAsync(TripRow row) => row.IsDocument ? Nav.DocumentAsync(row.Id) : Nav.GoAsync($"trip?id={row.Id}");
}

/// <summary>One trip: its documents in order, rename, delete the trip (documents stay in the vault).</summary>
public sealed partial class TripViewModel(VaultHost host, Dialogs dialogs) : ObservableObject, IQueryAttributable, ILoads
{
    private string _id = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Dates { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public ObservableCollection<DocumentRow> Documents { get; } = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _id = query["id"]?.ToString() ?? "";

    public async Task LoadAsync()
    {
        var s = host.Services;
        if (await s.Trips.GetAsync(_id) is not { IsDeleted: false } trip)
        {
            await Nav.BackAsync();
            return;
        }

        var view = (await s.TripService.ListAsync(trip.FamilyId)).First(v => v.Trip.Id == _id);
        var travelers = await s.Travelers.MapAsync(trip.FamilyId);
        Name = trip.Name;
        Dates = view.StartsUtc is { } a && view.EndsUtc is { } b ? TripStatuses.DateRange(a.LocalDateTime, b.LocalDateTime) : "";
        StatusText = view.Status(s.Session.Now) switch
        {
            TripStatus.Upcoming => AppResources.Trips_Upcoming,
            TripStatus.Ongoing => AppResources.Trips_Ongoing,
            _ => AppResources.Trips_Completed,
        };
        Documents.Clear();
        foreach (var doc in view.Documents)
        {
            Documents.Add(Display.Row(doc, travelers, s.Session.Now));
        }
    }

    [RelayCommand]
    private Task OpenAsync(DocumentRow row) => Nav.DocumentAsync(row.Id);

    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await dialogs.PromptAsync(AppResources.Trip_NameTitle, AppResources.Trip_NamePrompt, Name, maxLength: 60);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await host.Services.TripService.RenameAsync(_id, name);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await dialogs.ConfirmAsync(AppResources.Trip_DeleteTitle, AppResources.Trip_DeleteText, AppResources.Common_Delete))
        {
            await host.Services.TripService.DeleteAsync(_id);
            await Nav.BackAsync();
        }
    }
}

public sealed record FilterChip(string Label, DocumentType? Type, bool Selected);

/// <summary>
/// Global search (FR-S1): city (any name), airport/station code, passenger, provider, PNR, flight number, notes.
/// With an empty box it lists every document, filterable by type.
/// </summary>
public sealed partial class SearchViewModel(VaultHost host, ImportCoordinator importer) : ObservableObject, ILoads
{
    private CancellationTokenSource? _typing;
    private DocumentType? _type;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public ObservableCollection<DocumentRow> Results { get; } = [];

    public ObservableCollection<FilterChip> Filters { get; } = [];

    partial void OnQueryChanged(string value)
    {
        _typing?.Cancel();
        var cts = _typing = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(_ => MainThread.InvokeOnMainThreadAsync(LoadAsync), cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public async Task LoadAsync()
    {
        BuildFilters();
        if (await importer.CurrentFamilyAsync() is not { } familyId)
        {
            Results.Clear();
            return;
        }

        var s = host.Services;
        var docs = string.IsNullOrWhiteSpace(Query)
            ? (await s.Documents.ListAsync(familyId)).OrderByDescending(d => d.StartsUtc ?? d.CreatedAt).ToList()
            : (await s.Search.SearchAsync(familyId, Query)).ToList();
        if (_type is { } type)
        {
            docs = docs.Where(d => d.Type == type || (type == DocumentType.Passport && d.Type == DocumentType.Visa)).ToList();
        }

        var travelers = await s.Travelers.MapAsync(familyId);
        Results.Clear();
        foreach (var doc in docs.Take(200))
        {
            Results.Add(Display.Row(doc, travelers, s.Session.Now));
        }

        Summary = string.Format(AppResources.Search_Count, Results.Count);
    }

    private void BuildFilters()
    {
        Filters.Clear();
        Filters.Add(new FilterChip(AppResources.Search_All, null, _type is null));
        foreach (var (label, type) in new[]
                 {
                     (AppResources.Type_Flight, DocumentType.Flight), (AppResources.Type_Train, DocumentType.Train), (AppResources.Type_Bus, DocumentType.Bus),
                     (AppResources.Type_Hotel, DocumentType.Hotel), (AppResources.Type_Event, DocumentType.Event), (AppResources.Search_Ids, DocumentType.Passport),
                     (AppResources.Type_BoardingPass, DocumentType.BoardingPass),
                 })
        {
            Filters.Add(new FilterChip(label, type, _type == type));
        }
    }

    [RelayCommand]
    private async Task FilterAsync(FilterChip chip)
    {
        _type = chip.Type;
        await LoadAsync();
    }

    [RelayCommand]
    private Task OpenAsync(DocumentRow row) => Nav.DocumentAsync(row.Id);
}
