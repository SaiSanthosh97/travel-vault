using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.Views;
using TravelVault.Core.Documents;
using TravelVault.Core.Duplicates;
using TravelVault.Core.Trips;

using Visibility = TravelVault.Core.Documents.Visibility;

namespace TravelVault.App.ViewModels;

/// <summary>A label/value line; <see cref="Check"/> highlights values read with low confidence (FR-D6).</summary>
public sealed record FieldRow(string Label, string Value, bool Check);

public sealed record SegmentRow(string Route, string Departs, string Arrives, string Details, string Missing);

public sealed record FileRow(string Id, string Text, bool IsPrimary);

/// <summary>
/// One document: details with confidence highlights, legs, travelers, file versions, linked boarding passes and
/// who's still missing one, and every action (view, barcode, save, share, edit, travelers, private, trip, merge, delete).
/// </summary>
public sealed partial class DocumentViewModel(VaultHost host, AppSettings settings, FileActions files, Dialogs dialogs)
    : ObservableObject, IQueryAttributable, ILoads
{
    private TravelDocument? _doc;
    private string _id = "";
    private DuplicateCandidate? _duplicate;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Icon { get; set; } = "";

    [ObservableProperty]
    public partial string TypeText { get; set; } = "";

    [ObservableProperty]
    public partial string TravelersText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPrivate { get; set; }

    [ObservableProperty]
    public partial bool CanMakePrivate { get; set; }

    [ObservableProperty]
    public partial bool CanShowBarcode { get; set; }

    [ObservableProperty]
    public partial string TripText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDuplicate { get; set; }

    [ObservableProperty]
    public partial string DuplicateText { get; set; } = "";

    [ObservableProperty]
    public partial string Warning { get; set; } = "";

    public ObservableCollection<FieldRow> Fields { get; } = [];

    public ObservableCollection<SegmentRow> Segments { get; } = [];

    public ObservableCollection<FileRow> Files { get; } = [];

    public ObservableCollection<DocumentRow> BoardingPasses { get; } = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _id = Uri.UnescapeDataString(query["id"]?.ToString() ?? "");

    public async Task LoadAsync()
    {
        var s = host.Services;
        if (await s.Documents.GetAsync(_id) is not { IsDeleted: false } doc)
        {
            await Nav.BackAsync();
            return;
        }

        _doc = doc;
        var travelers = await s.Travelers.MapAsync(doc.FamilyId);
        var all = await s.Documents.ListAsync(doc.FamilyId);
        Title = doc.Title;
        Icon = Display.Icon(doc.Type);
        TypeText = Display.TypeName(doc.Type) + (doc.Provider is null ? "" : " · " + doc.Provider);
        TravelersText = Display.Travelers(doc, travelers);
        IsPrivate = doc.Visibility == Visibility.Private;
        CanMakePrivate = doc.OwnerMemberId == s.Session.MemberId;
        CanShowBarcode = doc.Type is DocumentType.BoardingPass or DocumentType.Event or DocumentType.Train or DocumentType.Bus or DocumentType.Flight;
        Services.Activity.SetSecure(IsPrivate || doc.Type is DocumentType.Passport or DocumentType.Visa, settings);

        Fields.Clear();
        Add(AppResources.Field_Pnr, doc.Pnr, TravelDocument.Fields.Pnr);
        Add(AppResources.Field_BookingRef, doc.BookingReference, TravelDocument.Fields.BookingReference);
        Add(AppResources.Field_Provider, doc.Provider, TravelDocument.Fields.Provider);
        if (doc.Identity is { } id)
        {
            Add(AppResources.Field_DocumentNumber, id.MaskedNumber, TravelDocument.Fields.Identity);
            Add(AppResources.Field_Country, id.VisaCountry ?? id.IssuingCountry, TravelDocument.Fields.Identity);
            Add(AppResources.Field_ValidFrom, id.ValidFrom?.ToString("d MMM yyyy", CultureInfo.CurrentCulture), TravelDocument.Fields.Identity);
            Add(AppResources.Field_Expires, id.ExpiresOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture), TravelDocument.Fields.Identity);
        }

        Add(AppResources.Field_Notes, doc.Notes, "notes");

        Segments.Clear();
        foreach (var segment in doc.Segments)
        {
            var missing = doc.Type == DocumentType.Flight ? BoardingPassLinker.MissingFor(doc, segment, all) : [];
            Segments.Add(new SegmentRow(
                Display.Route(segment),
                Display.When(segment.Departs),
                segment.Arrives is null ? "" : "→ " + Display.When(segment.Arrives),
                string.Join(" · ", new[] { segment.ServiceNumber, segment.Seat is null ? null : string.Format(AppResources.Home_Seat, segment.Seat), segment.TravelClass }.Where(x => !string.IsNullOrWhiteSpace(x))),
                missing.Count == 0 ? "" : string.Format(AppResources.Document_MissingPass, string.Join(", ", missing.Select(t => travelers.TryGetValue(t, out var tr) ? tr.DisplayName : "?")))));
        }

        Files.Clear();
        var present = await s.FileKeys.PresentAsync(doc.FamilyId);
        foreach (var file in doc.Files.OrderByDescending(f => f.AddedAt))
        {
            var where = present.Contains(file.Id) ? "" : " · " + AppResources.Document_NotOnPhone;
            Files.Add(new FileRow(file.Id, $"{file.OriginalName} · {Display.Size(file.Size)} · {file.AddedAt.LocalDateTime:d MMM yyyy}{where}", file.IsPrimary));
        }

        BoardingPasses.Clear();
        foreach (var pass in BoardingPassLinker.LinkedTo(doc, all))
        {
            BoardingPasses.Add(Display.Row(pass, travelers, s.Session.Now));
        }

        TripText = doc.TripId is { } tripId && await s.Trips.GetAsync(tripId) is { IsDeleted: false } trip
            ? string.Format(AppResources.Document_InTrip, trip.Name)
            : AppResources.Document_NoTrip;

        _duplicate = DuplicateDetector.Find(doc, all).FirstOrDefault(d => d.Strength == DuplicateStrength.Strong);
        HasDuplicate = _duplicate is not null;
        DuplicateText = _duplicate is null ? "" : string.Format(AppResources.Document_Duplicate, _duplicate.Existing.Title);
        Warning = doc.FieldConfidence.Values.Any(c => c == Confidence.Low) ? AppResources.Document_CheckWarning : "";

        void Add(string label, string? value, string field)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Fields.Add(new FieldRow(label, value, doc.ConfidenceOf(field) == Confidence.Low));
            }
        }
    }

    [RelayCommand]
    private Task ViewAsync() => Nav.GoAsync($"viewer?id={_id}");

    [RelayCommand]
    private Task BarcodeAsync() => Nav.GoAsync($"viewer?id={_id}&barcode=1");

    [RelayCommand]
    private Task OpenFileAsync(FileRow row) => Nav.GoAsync($"viewer?id={_id}&file={row.Id}");

    [RelayCommand]
    private async Task MakePrimaryAsync(FileRow row)
    {
        await host.Services.DocumentService.SetPrimaryFileAsync(_id, row.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private Task SaveAsync() => _doc is null ? Task.CompletedTask : files.SaveToPhoneAsync(_doc);

    [RelayCommand]
    private Task ShareAsync() => _doc is null ? Task.CompletedTask : files.ShareAsync(_doc);

    [RelayCommand]
    private Task EditAsync() => Nav.GoAsync($"edit?id={_id}");

    [RelayCommand]
    private Task OpenPassAsync(DocumentRow row) => Nav.DocumentAsync(row.Id);

    [RelayCommand]
    private async Task TravelersAsync()
    {
        if (_doc is null)
        {
            return;
        }

        var people = await host.Services.Travelers.ListAsync(_doc.FamilyId);
        var selected = _doc.AllTravelerIds().ToHashSet();
        while (true)
        {
            var options = people.Select(p => (selected.Contains(p.Id) ? "✓ " : "   ") + p.DisplayName).Append(AppResources.Document_NewTraveler).Append(AppResources.Common_Done).ToArray();
            var choice = await dialogs.ChooseAsync(AppResources.Document_WhoTravels, options);
            if (choice is null)
            {
                return;
            }

            if (choice == AppResources.Common_Done)
            {
                break;
            }

            if (choice == AppResources.Document_NewTraveler)
            {
                var name = await dialogs.PromptAsync(AppResources.Travelers_Add, AppResources.Travelers_NamePrompt, maxLength: 60);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var created = await host.Services.Travelers.CreateAsync(_doc.FamilyId, name.Trim());
                    people = await host.Services.Travelers.ListAsync(_doc.FamilyId);
                    selected.Add(created.Id);
                }

                continue;
            }

            var person = people[Array.IndexOf(options, choice)];
            if (!selected.Remove(person.Id))
            {
                selected.Add(person.Id);
            }
        }

        if (selected.Count == 0)
        {
            await dialogs.AlertAsync(AppResources.Document_WhoTravels, AppResources.Document_NeedTraveler);
            return;
        }

        await host.Services.DocumentService.ReassignTravelersAsync(_id, [.. selected]);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task TogglePrivateAsync()
    {
        if (_doc is null)
        {
            return;
        }

        var makePrivate = _doc.Visibility == Visibility.Family;
        if (!await dialogs.ConfirmAsync(AppResources.Document_PrivacyTitle, makePrivate ? AppResources.Document_MakePrivateText : AppResources.Document_MakeSharedText, AppResources.Common_OK))
        {
            return;
        }

        await host.Services.DocumentService.SetVisibilityAsync(_id, makePrivate ? Visibility.Private : Visibility.Family);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task TripAsync()
    {
        if (_doc is null)
        {
            return;
        }

        var s = host.Services;
        var trips = await s.TripService.ListAsync(_doc.FamilyId);
        var options = trips.Select(t => t.Trip.Name).Append(AppResources.Document_NewTrip).ToList();
        if (_doc.TripId is not null)
        {
            options.Add(AppResources.Document_RemoveFromTrip);
        }

        var choice = await dialogs.ChooseAsync(AppResources.Document_TripTitle, [.. options]);
        if (choice is null)
        {
            return;
        }

        if (choice == AppResources.Document_RemoveFromTrip)
        {
            await s.TripService.RemoveDocumentAsync(_id);
        }
        else if (choice == AppResources.Document_NewTrip)
        {
            var name = await dialogs.PromptAsync(AppResources.Trip_NameTitle, AppResources.Trip_NamePrompt, _doc.Segments.FirstOrDefault()?.To?.City ?? _doc.Title, maxLength: 60);
            if (!string.IsNullOrWhiteSpace(name))
            {
                await s.TripService.CreateAsync(_doc.FamilyId, name, [_id]);
            }
        }
        else if (trips.FirstOrDefault(t => t.Trip.Name == choice) is { } trip)
        {
            await s.TripService.AddDocumentAsync(trip.Trip.Id, _id);
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (_duplicate is null || !await dialogs.ConfirmAsync(AppResources.Document_MergeTitle, string.Format(AppResources.Document_MergeText, _duplicate.Existing.Title), AppResources.Document_Merge))
        {
            return;
        }

        // Keep the older document (its trip, edits and history); this one's file becomes a newer version of it.
        var keep = await host.Services.DocumentService.MergeAsync(_duplicate.Existing.Id, _id);
        await Nav.BackAsync();
        await Nav.DocumentAsync(keep.Id);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await dialogs.ConfirmAsync(AppResources.Document_DeleteTitle, AppResources.Document_DeleteText, AppResources.Common_Delete))
        {
            await host.Services.DocumentService.DeleteAsync(_id);
            Dialogs.Toast(AppResources.Document_Deleted);
            await Nav.BackAsync();
        }
    }
}
