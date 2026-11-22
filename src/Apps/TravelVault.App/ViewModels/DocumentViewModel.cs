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

/// <summary>
/// One leg of the ticket card, laid out like a printed ticket: big codes (or names) on both ends, times under them,
/// the travel time in the middle. A stay uses the two ends for check-in and check-out; an event has only the start.
/// </summary>
public sealed record TicketLeg(
    string FromCode, string FromName, string DepartTime, string DepartDate,
    string ToCode, string ToName, string ArriveTime, string ArriveDate,
    string Middle, string ModeIcon, bool HasEnd, string Missing)
{
    public bool HasMissing => Missing.Length > 0;
}

/// <summary>A small labelled value on the ticket strip ("PNR 4521378965", "Berth 56").</summary>
public sealed record TicketChip(string Label, string Value, bool Check);

/// <summary>A traveler on this ticket and what the ticket says about them ("Confirmed · Coach S4 · Berth 56").</summary>
public sealed record PassengerLine(string Name, string Status)
{
    public bool HasStatus => Status.Length > 0;
}

public sealed record FileRow(string Id, string Text, bool IsPrimary);

/// <summary>
/// One document, shown as a ticket card (provider and status, the route with big times, a strip with PNR/class/seat,
/// the passengers with their seats) followed by trip, files and actions. Everything else that was read from the
/// ticket is folded under "More details" so the important parts are visible without scrolling.
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

    /// <summary>Top band of the card: the provider ("IRCTC", "IndiGo", the hotel) or the document type.</summary>
    [ObservableProperty]
    public partial string HeaderTitle { get; set; } = "";

    /// <summary>Service under the provider: "20824 · AII PURI SF EXP", "6E 2345".</summary>
    [ObservableProperty]
    public partial string HeaderSubtitle { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial Color StatusColor { get; set; } = Colors.Transparent;

    [ObservableProperty]
    public partial bool ShowMore { get; set; }

    [ObservableProperty]
    public partial string MoreText { get; set; } = AppResources.Ticket_MoreDetails;

    public ObservableCollection<TicketLeg> Legs { get; } = [];

    public ObservableCollection<TicketChip> Chips { get; } = [];

    public ObservableCollection<PassengerLine> Passengers { get; } = [];

    /// <summary>Everything else that was read (booking reference, quota, distance, transaction id, notes…).</summary>
    public ObservableCollection<FieldRow> Fields { get; } = [];

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

        BuildCard(doc, all, travelers);

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
    }

    private void BuildCard(TravelDocument doc, IReadOnlyList<TravelDocument> all, IReadOnlyDictionary<string, Core.Travelers.Traveler> travelers)
    {
        var first = doc.Segments.FirstOrDefault();
        HeaderTitle = doc.Provider ?? Display.TypeName(doc.Type);
        HeaderSubtitle = doc.Segments.Count == 1 && first is not null
            ? string.Join(" · ", new[] { first.ServiceNumber, first.ServiceName }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : Display.TypeName(doc.Type);
        if (HeaderSubtitle.Length == 0 || HeaderSubtitle == HeaderTitle)
        {
            HeaderSubtitle = Display.TypeName(doc.Type);
        }

        var details = doc.Details.ToList();
        var status = Take("Status");
        StatusText = status?.ToUpper(CultureInfo.CurrentCulture) ?? "";
        StatusColor = status switch
        {
            null => Colors.Transparent,
            _ when status.StartsWith("Confirmed", StringComparison.OrdinalIgnoreCase) => Display.Ok,
            _ when status.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase) => Display.Danger,
            _ => Display.Warning,
        };

        Legs.Clear();
        foreach (var segment in doc.Segments)
        {
            var missing = doc.Type == DocumentType.Flight ? BoardingPassLinker.MissingFor(doc, segment, all) : [];
            var missingText = missing.Count == 0 ? "" : string.Format(AppResources.Document_MissingPass, string.Join(", ", missing.Select(t => travelers.TryGetValue(t, out var tr) ? tr.DisplayName : "?")));
            Legs.Add(Leg(doc, segment, doc.Segments.Count > 1, missingText));
        }

        // The strip: what you are asked for at the gate or by the TTE.
        Chips.Clear();
        Chip(AppResources.Field_Pnr, doc.Pnr, TravelDocument.Fields.Pnr);
        if (doc.Pnr is null)
        {
            Chip(AppResources.Field_BookingRef, doc.BookingReference, TravelDocument.Fields.BookingReference);
        }

        if (first is not null && doc.Segments.Count == 1)
        {
            if (doc.Type == DocumentType.Train && first.Seat?.Split('/') is [var coach, var berth])
            {
                // Chair cars and second sitting (coaches C…, D…, E…) have seats, not berths.
                var seated = coach.Length > 0 && coach[0] is 'C' or 'D' or 'E'
                    || ShortClass(first.TravelClass) is "CC" or "EC" or "EA" or "2S";
                Chip(AppResources.Field_Coach, coach, TravelDocument.Fields.Segments);
                Chip(seated ? AppResources.Field_Seat : AppResources.Field_Berth, berth, TravelDocument.Fields.Segments);
            }
            else
            {
                Chip(AppResources.Field_Seat, first.Seat, TravelDocument.Fields.Segments);
            }

            Chip(AppResources.Field_Class, ShortClass(first.TravelClass), TravelDocument.Fields.Segments);
        }

        foreach (var label in new[] { "Gate", "Terminal", "Boarding time", "PIN", "Nights" })
        {
            Chip(label, Take(label), TravelDocument.Fields.Segments);
        }

        if (doc.Identity is { } id)
        {
            Chip(AppResources.Field_DocumentNumber, id.MaskedNumber, TravelDocument.Fields.Identity);
            Chip(AppResources.Field_Country, id.VisaCountry ?? id.IssuingCountry, TravelDocument.Fields.Identity);
            Chip(AppResources.Field_ValidFrom, id.ValidFrom?.ToString("d MMM yyyy", CultureInfo.CurrentCulture), TravelDocument.Fields.Identity);
            Chip(AppResources.Field_Expires, id.ExpiresOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture), TravelDocument.Fields.Identity);
        }

        // Passengers with what the ticket says about each one (matched by the name as printed).
        Passengers.Clear();
        foreach (var travelerId in doc.AllTravelerIds())
        {
            var name = travelers.TryGetValue(travelerId, out var t) ? t.DisplayName : "?";
            var printed = doc.Passengers.FirstOrDefault(p => p.TravelerId == travelerId)?.RawName;
            var line = printed is null ? null : details.FirstOrDefault(d => string.Equals(d.Label, printed, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                details.Remove(line);
            }

            Passengers.Add(new PassengerLine(name, line?.Value ?? ""));
        }

        // Everything else, folded away.
        Fields.Clear();
        Add(AppResources.Field_BookingRef, doc.Pnr is null ? null : doc.BookingReference, TravelDocument.Fields.BookingReference);
        if (doc.Segments.Count == 1 && first?.TravelClass is { } cls && ShortClass(cls) != cls)
        {
            Add(AppResources.Field_Class, cls, TravelDocument.Fields.Segments);
        }

        foreach (var d in details)
        {
            Add(d.Label, d.Value, "details");
        }

        Add(AppResources.Field_Notes, doc.Notes, "notes");
        ShowMore = false;
        MoreText = AppResources.Ticket_MoreDetails;

        string? Take(string label)
        {
            var found = details.FirstOrDefault(d => d.Label == label);
            if (found is not null)
            {
                details.Remove(found);
            }

            return found?.Value;
        }

        void Chip(string label, string? value, string field)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Chips.Add(new TicketChip(label.ToUpper(CultureInfo.CurrentCulture), value, false)); // everything was confirmed on the form (D64): nothing is orange
            }
        }

        void Add(string label, string? value, string field)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Fields.Add(new FieldRow(label, value, false)); // everything was confirmed on the form (D64): nothing is orange
            }
        }
    }

    /// <summary>"SLEEPER CLASS (SL)" → "SL", "AC 3 TIER (3A)" → "3A": the code fits the strip; the full name is under More details.</summary>
    private static string? ShortClass(string? travelClass)
    {
        if (travelClass is null)
        {
            return null;
        }

        var open = travelClass.LastIndexOf('(');
        var close = travelClass.LastIndexOf(')');
        return open >= 0 && close > open + 1 && close - open <= 6 ? travelClass[(open + 1)..close] : travelClass;
    }

    private static TicketLeg Leg(TravelDocument doc, Segment s, bool several, string missing)
    {
        var icon = Display.Icon(doc.Type == DocumentType.BoardingPass ? DocumentType.Flight : doc.Type);
        switch (s.Mode)
        {
            case TravelMode.Stay:
                var nights = s.Departs is { } i && s.Arrives is { } o ? (DateOnly.FromDateTime(o.Local).DayNumber - DateOnly.FromDateTime(i.Local).DayNumber) : 0;
                return new TicketLeg(
                    AppResources.Ticket_CheckIn, s.From.City ?? "", Time(s.Departs), Day(s.Departs),
                    AppResources.Ticket_CheckOut, "", Time(s.Arrives), Day(s.Arrives),
                    nights <= 0 ? "" : string.Format(nights == 1 ? AppResources.Ticket_Night : AppResources.Ticket_Nights, nights), icon, s.Arrives is not null, missing);
            case TravelMode.Event:
                return new TicketLeg(Name(s.From), s.From.City ?? "", Time(s.Departs), Day(s.Departs), "", "", "", "", "", icon, false, missing);
            default:
                var middle = Duration(s) ?? (several ? s.ServiceNumber ?? "" : "");
                return new TicketLeg(
                    Code(s.From), Under(s.From), Time(s.Departs), Day(s.Departs),
                    s.To is null ? "" : Code(s.To), s.To is null ? "" : Under(s.To), Time(s.Arrives), Day(s.Arrives),
                    several && s.ServiceNumber is { } n && middle != n ? $"{n} · {middle}" : middle, icon, s.To is not null, missing);
        }

        static string Code(Place p) => p.Code ?? Name(p);

        // Under the code: the city ("Ajmer" under AII); when there is no code the name is already big.
        static string Under(Place p) => p.Code is null ? "" : p.City ?? Title(p.Name);

        static string Name(Place p) => p.City is { Length: > 0 } city && p.Code is null && p.Name.Length > 24 ? city : p.Name;

        static string Title(string s) => s.Any(char.IsLower) ? s : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

        static string Time(LocalDateTimeZ? t) => t is { HasTime: true } v ? v.Local.ToString("HH:mm", CultureInfo.CurrentCulture) : "";

        static string Day(LocalDateTimeZ? t) => t is { } v ? v.Local.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture) : "";

        static string? Duration(Segment s)
        {
            if (s.Departs is not { HasTime: true } d || s.Arrives is not { HasTime: true } a)
            {
                return null;
            }

            // Both ends in their own time zones when known (a flight Bengaluru → Dubai), otherwise as printed.
            var span = d.TimeZoneId is not null && a.TimeZoneId is not null ? a.ToUtc() - d.ToUtc() : a.Local - d.Local;
            return span <= TimeSpan.Zero || span > TimeSpan.FromDays(4) ? null
                : string.Format(CultureInfo.CurrentCulture, AppResources.Ticket_Duration, (int)span.TotalHours, span.Minutes);
        }
    }

    [RelayCommand]
    private void ToggleMore()
    {
        ShowMore = !ShowMore;
        MoreText = ShowMore ? AppResources.Ticket_LessDetails : AppResources.Ticket_MoreDetails;
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
