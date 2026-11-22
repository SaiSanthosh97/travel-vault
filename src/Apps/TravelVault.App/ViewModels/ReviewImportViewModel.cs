using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.Core.Documents;
using TravelVault.Core.Duplicates;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;
using TravelVault.Core.Vault;
using F = TravelVault.Core.Documents.TravelDocument.Fields;

namespace TravelVault.App.ViewModels;

/// <summary>Who a printed name is: an existing traveler, a new profile, or nobody (ignore).</summary>
public sealed partial class NameChoice : ObservableObject
{
    public required string RawName { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public required IReadOnlyList<string?> TravelerIds { get; init; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    /// <summary>Options: travelers…, "New traveler", "Not a traveler".</summary>
    public NameDecision Decision() =>
        SelectedIndex == Options.Count - 1 ? new NameDecision(RawName, null, Ignore: true)
        : SelectedIndex == Options.Count - 2 ? new NameDecision(RawName, null, CreateNew: true)
        : new NameDecision(RawName, TravelerIds[SelectedIndex]);
}

/// <summary>
/// The short form every added ticket goes through (D63): mode of transport, from, to, departure and arrival, who travels,
/// seat. What the app read is pre-filled; the person confirms or corrects it, so nothing is saved unseen and nothing
/// needs an orange "check this" mark (D64). After saving a journey it asks about the return or onward journey (D65).
/// </summary>
public sealed partial class ReviewImportViewModel(VaultHost host, ImportCoordinator importer, Dialogs dialogs) : ObservableObject
{
    /// <summary>The kinds offered on the form, in the order people need them.</summary>
    private static readonly DocumentType[] Kinds =
    [
        DocumentType.Flight, DocumentType.Train, DocumentType.Bus, DocumentType.Hotel, DocumentType.Event,
        DocumentType.Passport, DocumentType.Visa, DocumentType.BoardingPass, DocumentType.Other,
    ];

    private ImportDraft? _draft;
    private DuplicateCandidate? _duplicate;
    private IReadOnlyList<Traveler> _travelers = [];

    public IReadOnlyList<string> KindNames { get; } = Kinds.Select(Display.TypeName).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IsJourney), nameof(IsStay), nameof(IsEvent), nameof(HasLegs), nameof(IsIdentity),
        nameof(ProviderLabel), nameof(FromLabel), nameof(DepartLabel), nameof(ArriveLabel), nameof(NumberLabel), nameof(HasArrival))]
    public partial int KindIndex { get; set; }

    public DocumentType Kind => Kinds[Math.Clamp(KindIndex, 0, Kinds.Length - 1)];

    public bool IsJourney => Kind is DocumentType.Flight or DocumentType.Train or DocumentType.Bus or DocumentType.BoardingPass or DocumentType.Other;

    public bool IsStay => Kind == DocumentType.Hotel;

    public bool IsEvent => Kind == DocumentType.Event;

    public bool HasLegs => IsJourney || IsStay || IsEvent;

    public bool IsIdentity => Kind is DocumentType.Passport or DocumentType.Visa;

    public bool HasArrival => IsJourney || IsStay;

    public string ProviderLabel => Kind switch
    {
        DocumentType.Hotel => AppResources.Form_HotelName,
        DocumentType.Event => AppResources.Form_EventName,
        DocumentType.Flight or DocumentType.BoardingPass => AppResources.Form_Airline,
        DocumentType.Train => AppResources.Form_TrainName,
        _ => AppResources.Form_Operator,
    };

    public string FromLabel => Kind switch
    {
        DocumentType.Hotel => AppResources.Form_City,
        DocumentType.Event => AppResources.Form_Venue,
        _ => AppResources.Edit_From,
    };

    public string DepartLabel => Kind switch
    {
        DocumentType.Hotel => AppResources.Ticket_CheckIn,
        DocumentType.Event => AppResources.Form_Starts,
        _ => AppResources.Form_Departs,
    };

    public string ArriveLabel => IsStay ? AppResources.Ticket_CheckOut : AppResources.Form_Arrives;

    public string NumberLabel => Kind switch
    {
        DocumentType.Flight or DocumentType.BoardingPass => AppResources.Form_FlightNumber,
        DocumentType.Train => AppResources.Form_TrainNumber,
        _ => AppResources.Edit_Number,
    };

    [ObservableProperty]
    public partial string Provider { get; set; } = "";

    [ObservableProperty]
    public partial string Pnr { get; set; } = "";

    [ObservableProperty]
    public partial string IdentityNumber { get; set; } = "";

    [ObservableProperty]
    public partial string IdentityCountry { get; set; } = "";

    [ObservableProperty]
    public partial DateTime ExpiresOn { get; set; } = DateTime.Today.AddYears(1);

    [ObservableProperty]
    public partial bool HasDuplicate { get; set; }

    [ObservableProperty]
    public partial string DuplicateText { get; set; } = "";

    [ObservableProperty]
    public partial bool Merge { get; set; } = true;

    /// <summary>No name was printed (or read): the person picks who travels.</summary>
    [ObservableProperty]
    public partial bool NoNames { get; set; }

    public ObservableCollection<string> TravelerNames { get; } = [];

    [ObservableProperty]
    public partial int TravelerIndex { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<SegmentEdit> Legs { get; } = [];

    public ObservableCollection<NameChoice> Names { get; } = [];

    partial void OnKindIndexChanged(int value)
    {
        var mode = EditDocumentViewModel.ModeFor(Kind);
        foreach (var leg in Legs)
        {
            leg.Mode = mode;
            leg.HasArrival = HasArrival;
        }

        if (HasLegs && Legs.Count == 0)
        {
            AddLeg();
        }
    }

    public async Task LoadAsync()
    {
        _draft = importer.CurrentDraft;
        if (_draft is null)
        {
            await Nav.BackAsync();
            return;
        }

        var doc = _draft.Document;
        Provider = doc.Provider ?? "";
        Pnr = doc.Pnr ?? doc.BookingReference ?? "";
        IdentityNumber = doc.Identity?.Number ?? "";
        IdentityCountry = doc.Identity?.VisaCountry ?? doc.Identity?.IssuingCountry ?? "";
        ExpiresOn = doc.Identity?.ExpiresOn?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today.AddYears(1);

        Legs.Clear();
        foreach (var segment in doc.Segments)
        {
            var leg = SegmentEdit.From(segment);
            if (segment.Arrives is null)
            {
                // Arrival not read: start from the departure so only the time needs changing.
                leg.ArriveDate = leg.DepartDate;
                leg.ArriveTime = leg.DepartTime;
            }

            Legs.Add(leg);
        }

        var kind = Array.IndexOf(Kinds, doc.Type);
        KindIndex = kind < 0 ? Array.IndexOf(Kinds, DocumentType.Other) : kind;
        OnKindIndexChanged(KindIndex); // also when the index didn't change: sets arrival visibility and adds an empty leg

        _travelers = await host.Services.Travelers.ListAsync(doc.FamilyId);
        Names.Clear();
        foreach (var match in _draft.Names)
        {
            var options = _travelers.Select(t => t.DisplayName).ToList();
            var ids = _travelers.Select(t => (string?)t.Id).ToList();
            options.Add(string.Format(AppResources.Review_NewTraveler, PersonName.Parse(match.RawName).ToTitleCase()));
            ids.Add(null);
            options.Add(AppResources.Review_NotTraveler);
            ids.Add(null);
            Names.Add(new NameChoice
            {
                RawName = match.RawName,
                Options = options,
                TravelerIds = ids,
                SelectedIndex = match.Traveler is { } t ? ids.IndexOf(t.Id) : options.Count - 2,
            });
        }

        NoNames = Names.Count == 0;
        TravelerNames.Clear();
        if (NoNames)
        {
            var me = await host.Services.Travelers.MyTravelerAsync(doc.FamilyId);
            _travelers = await host.Services.Travelers.ListAsync(doc.FamilyId);
            foreach (var t in _travelers)
            {
                TravelerNames.Add(t.DisplayName);
            }

            TravelerIndex = Math.Max(0, _travelers.ToList().FindIndex(t => t.Id == me.Id));
        }

        _duplicate = _draft.Duplicates.FirstOrDefault();
        HasDuplicate = _duplicate is not null;
        Merge = _duplicate?.Strength == DuplicateStrength.Strong;
        DuplicateText = _duplicate is null ? "" : string.Format(
            _duplicate.Strength == DuplicateStrength.Strong ? AppResources.Review_DuplicateStrong : AppResources.Review_DuplicateWeak, _duplicate.Existing.Title);
    }

    [RelayCommand]
    private void AddLeg()
    {
        var last = Legs.LastOrDefault();
        var leg = SegmentEdit.From(new Segment { Id = Core.Common.Ids.New(), Mode = EditDocumentViewModel.ModeFor(Kind), From = last?.Original.To ?? new Place("") });
        leg.FromName = last?.ToName ?? "";
        leg.FromCode = last?.ToCode ?? "";
        leg.DepartDate = last?.ArriveDate ?? DateTime.Today;
        leg.ArriveDate = leg.DepartDate;
        leg.HasArrival = HasArrival;
        Legs.Add(leg);
    }

    [RelayCommand]
    private void RemoveLeg(SegmentEdit leg)
    {
        if (Legs.Count > 1)
        {
            Legs.Remove(leg);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_draft is null || IsBusy)
        {
            return;
        }

        if (HasLegs && Legs.Any(l => l.FromName.Trim().Length == 0 && l.FromCode.Trim().Length == 0
                                     || (l.HasDestination && l.ToName.Trim().Length == 0 && l.ToCode.Trim().Length == 0)))
        {
            await dialogs.AlertAsync(AppResources.Review_Title, IsJourney ? AppResources.Form_NeedFromTo : AppResources.Form_NeedPlace);
            return;
        }

        IsBusy = true;
        try
        {
            Apply(_draft.Document);
            var decisions = NoNames && _travelers.Count > 0
                ? [new NameDecision(_travelers[Math.Clamp(TravelerIndex, 0, _travelers.Count - 1)].DisplayName, _travelers[Math.Clamp(TravelerIndex, 0, _travelers.Count - 1)].Id)]
                : Names.Select(n => n.Decision()).ToList();
            var saved = await host.Services.Import.SaveAsync(_draft, decisions, HasDuplicate && Merge ? _duplicate!.Existing.Id : null);
            Dialogs.Toast(string.Format(AppResources.Import_Saved, saved.Title));
            importer.DraftFinished();
            await AfterSaveAsync(saved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await dialogs.AlertAsync(AppResources.Import_Title, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Copies the form into the document. Everything on it was seen by the person, so nothing stays "uncertain".</summary>
    private void Apply(TravelDocument doc)
    {
        Confirm(F.Type, doc.Type != Kind, () => doc.Type = Kind);
        Confirm(F.Provider, doc.Provider != Blank(Provider), () => doc.Provider = Blank(Provider));
        var code = BookingCodes.Normalize(Pnr);
        if (doc.Pnr is null && doc.BookingReference is not null && code == BookingCodes.Normalize(doc.BookingReference))
        {
            Confirm(F.BookingReference, false, () => { }); // the field showed the booking reference, unchanged
        }
        else
        {
            Confirm(F.Pnr, doc.Pnr != code, () => doc.Pnr = code);
        }

        if (HasLegs)
        {
            var legs = Legs.Select(l => l.ToSegment()).ToList();
            Confirm(F.Segments, true, () => doc.Segments = legs);
        }
        else
        {
            doc.Segments = [];
        }

        if (IsIdentity)
        {
            var identity = (doc.Identity ?? new IdentityInfo(null, null, null, null)) with
            {
                Number = Blank(IdentityNumber),
                ExpiresOn = DateOnly.FromDateTime(ExpiresOn),
            };
            identity = Kind == DocumentType.Visa ? identity with { VisaCountry = Blank(IdentityCountry) } : identity with { IssuingCountry = Blank(IdentityCountry) };
            Confirm(F.Identity, identity != doc.Identity, () => doc.Identity = identity);
        }

        doc.FieldConfidence[F.Passengers] = Confidence.UserEdited;
        DocumentTitles.Refresh(doc);

        void Confirm(string field, bool changed, Action apply)
        {
            apply();
            doc.FieldConfidence[field] = changed ? Confidence.UserEdited : Confidence.High;
        }
    }

    /// <summary>
    /// Grouping (D65): a ticket added for a trip joins it; otherwise a journey asks "Is there a return or onward journey?".
    /// </summary>
    private async Task AfterSaveAsync(TravelDocument saved)
    {
        var trips = host.Services.TripService;
        if (importer.PendingTripId is { } pending && saved.TripId is null)
        {
            await trips.AddDocumentAsync(pending, saved.Id);
            await Nav.GoAsync($"../trip?id={pending}");
            if (await dialogs.ConfirmAsync(AppResources.Group_Title, AppResources.Group_AnotherQuestion, AppResources.Group_AddAnother, AppResources.Group_Done))
            {
                await importer.AddAsync();
            }
            else
            {
                importer.PendingTripId = null;
            }

            return;
        }

        importer.PendingTripId = null;
        if (saved.TripId is not null || saved.Type is DocumentType.Passport or DocumentType.Visa or DocumentType.Other || saved.Segments.Count == 0)
        {
            await Nav.GoAsync($"../document?id={saved.Id}");
            return;
        }

        var open = (await trips.ListAsync(saved.FamilyId)).Where(t => t.Status(host.Services.Session.Now) != TripStatus.Completed).ToList();
        var options = new List<string> { AppResources.Group_AddNext };
        if (open.Count > 0)
        {
            options.Add(AppResources.Group_AddToExisting);
        }

        options.Add(AppResources.Group_No);
        var choice = await dialogs.ChooseAsync(AppResources.Group_Question, [.. options]);
        if (choice == AppResources.Group_AddNext)
        {
            var name = await dialogs.PromptAsync(AppResources.Trip_NameTitle, AppResources.Trip_NamePrompt, DefaultTripName(saved), maxLength: 60);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var trip = await trips.CreateAsync(saved.FamilyId, name, [saved.Id]);
                importer.PendingTripId = trip.Id;
                await Nav.GoAsync($"../trip?id={trip.Id}");
                await importer.AddAsync();
                return;
            }
        }
        else if (choice == AppResources.Group_AddToExisting)
        {
            var picked = await dialogs.ChooseAsync(AppResources.Group_PickTrip, [.. open.Select(t => t.Trip.Name)]);
            if (open.FirstOrDefault(t => t.Trip.Name == picked) is { } trip)
            {
                await trips.AddDocumentAsync(trip.Trip.Id, saved.Id);
                await Nav.GoAsync($"../trip?id={trip.Trip.Id}");
                return;
            }
        }

        await Nav.GoAsync($"../document?id={saved.Id}");
    }

    /// <summary>"Trip to Hyderabad": where the journey goes (for a round trip on one ticket, the far end, not home).</summary>
    internal static string DefaultTripName(TravelDocument doc)
    {
        var legs = doc.Segments;
        var last = legs[^1];
        var far = legs.Count > 1 && last.To?.Code is { } end && end == legs[0].From.Code ? legs[(legs.Count / 2) - 1].To : last.To ?? last.From;
        var place = far?.City ?? far?.Name;
        return string.IsNullOrWhiteSpace(place) ? AppResources.Trip_DefaultName : string.Format(AppResources.Trip_To, place);
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        importer.DraftFinished();
        importer.PendingTripId = null;
        await Nav.BackAsync();
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
