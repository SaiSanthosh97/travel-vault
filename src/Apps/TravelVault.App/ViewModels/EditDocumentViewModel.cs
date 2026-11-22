using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;
using F = TravelVault.Core.Documents.TravelDocument.Fields;

namespace TravelVault.App.ViewModels;

/// <summary>One leg being edited. A recognised airport code fills in the city and time zone.</summary>
public sealed partial class SegmentEdit : ObservableObject
{
    public required Segment Original { get; init; }

    [ObservableProperty]
    public partial string FromName { get; set; } = "";

    [ObservableProperty]
    public partial string FromCode { get; set; } = "";

    [ObservableProperty]
    public partial string ToName { get; set; } = "";

    [ObservableProperty]
    public partial string ToCode { get; set; } = "";

    [ObservableProperty]
    public partial DateTime DepartDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial TimeSpan DepartTime { get; set; }

    [ObservableProperty]
    public partial bool HasArrival { get; set; }

    [ObservableProperty]
    public partial DateTime ArriveDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial TimeSpan ArriveTime { get; set; }

    [ObservableProperty]
    public partial string ServiceNumber { get; set; } = "";

    [ObservableProperty]
    public partial string Seat { get; set; } = "";

    /// <summary>Follows the document type chosen on the form (a ticket read as a train can be changed to a flight).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestination))]
    public partial TravelMode Mode { get; set; }

    public bool HasDestination => Mode is TravelMode.Flight or TravelMode.Train or TravelMode.Bus or TravelMode.Other;

    public static SegmentEdit From(Segment s) => new()
    {
        Original = s,
        Mode = s.Mode,
        FromName = s.From.City ?? s.From.Name,
        FromCode = s.From.Code ?? "",
        ToName = s.To?.City ?? s.To?.Name ?? "",
        ToCode = s.To?.Code ?? "",
        DepartDate = s.Departs?.Local.Date ?? DateTime.Today,
        DepartTime = s.Departs?.Local.TimeOfDay ?? TimeSpan.Zero,
        HasArrival = s.Arrives is not null,
        ArriveDate = s.Arrives?.Local.Date ?? s.Departs?.Local.Date ?? DateTime.Today,
        ArriveTime = s.Arrives?.Local.TimeOfDay ?? TimeSpan.Zero,
        ServiceNumber = s.ServiceNumber ?? "",
        Seat = s.Seat ?? "",
    };

    public Segment ToSegment()
    {
        var from = PlaceFor(FromName, FromCode, Original.From);
        var to = HasDestination && (ToName.Length > 0 || ToCode.Length > 0) ? PlaceFor(ToName, ToCode, Original.To) : Original.To;
        var departs = DepartDate.Date + DepartTime;
        var arrives = ArriveDate.Date + ArriveTime;
        return new Segment
        {
            Id = Original.Id,
            Mode = Mode,
            From = from,
            To = HasDestination ? to : null,
            Departs = new LocalDateTimeZ(departs, from.TimeZoneId),
            Arrives = HasArrival && arrives != departs ? new LocalDateTimeZ(arrives, (to ?? from).TimeZoneId) : null, // untouched = not given
            CarrierCode = Original.CarrierCode,
            ServiceNumber = Blank(ServiceNumber),
            ServiceName = Blank(ServiceNumber) == Original.ServiceNumber ? Original.ServiceName : null,
            Seat = Blank(Seat),
            TravelClass = Original.TravelClass,
            TravelerIds = Original.TravelerIds,
        };
    }

    private static Place PlaceFor(string name, string code, Place? original)
    {
        if (AirportDirectory.Instance.Find(code.Trim().ToUpperInvariant()) is { } airport)
        {
            return airport.ToPlace();
        }

        var trimmed = name.Trim();
        if (original is not null && string.Equals(trimmed, original.City ?? original.Name, StringComparison.OrdinalIgnoreCase))
        {
            return original with { Code = Blank(code) };
        }

        var city = AirportDirectory.Instance.FindByCity(trimmed).FirstOrDefault();
        return new Place(trimmed, Blank(code), trimmed, city?.CountryCode ?? original?.CountryCode, city?.TimeZoneId ?? original?.TimeZoneId);
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Editing a document by hand (FR-D5). Edited fields become "UserEdited" and are protected from automatic changes.</summary>
public sealed partial class EditDocumentViewModel(VaultHost host, Dialogs dialogs) : ObservableObject, IQueryAttributable
{
    private TravelDocument? _doc;
    private string _id = "";
    private string _originalTitle = "";

    public IReadOnlyList<string> TypeNames { get; } = Enum.GetValues<DocumentType>().Select(Display.TypeName).ToList();

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial int TypeIndex { get; set; }

    [ObservableProperty]
    public partial string Provider { get; set; } = "";

    [ObservableProperty]
    public partial string Pnr { get; set; } = "";

    [ObservableProperty]
    public partial string BookingReference { get; set; } = "";

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    [ObservableProperty]
    public partial bool HasIdentity { get; set; }

    [ObservableProperty]
    public partial string IdentityNumber { get; set; } = "";

    [ObservableProperty]
    public partial DateTime ExpiresOn { get; set; } = DateTime.Today.AddYears(1);

    public ObservableCollection<SegmentEdit> Segments { get; } = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _id = query["id"]?.ToString() ?? "";

    public async Task LoadAsync()
    {
        if (await host.Services.Documents.GetAsync(_id) is not { } doc)
        {
            return;
        }

        _doc = doc;
        Title = _originalTitle = doc.Title;
        TypeIndex = Array.IndexOf(Enum.GetValues<DocumentType>(), doc.Type);
        Provider = doc.Provider ?? "";
        Pnr = doc.Pnr ?? "";
        BookingReference = doc.BookingReference ?? "";
        Notes = doc.Notes ?? "";
        HasIdentity = doc.Type is DocumentType.Visa or DocumentType.Passport || doc.Identity is not null;
        IdentityNumber = doc.Identity?.Number ?? "";
        ExpiresOn = doc.Identity?.ExpiresOn?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today.AddYears(1);
        Segments.Clear();
        foreach (var segment in doc.Segments)
        {
            Segments.Add(SegmentEdit.From(segment));
        }
    }

    [RelayCommand]
    private void AddLeg()
    {
        var mode = _doc?.Type switch
        {
            DocumentType.Flight or DocumentType.BoardingPass => TravelMode.Flight,
            DocumentType.Train => TravelMode.Train,
            DocumentType.Bus => TravelMode.Bus,
            DocumentType.Hotel => TravelMode.Stay,
            DocumentType.Event => TravelMode.Event,
            _ => TravelMode.Other,
        };
        Segments.Add(SegmentEdit.From(new Segment { Id = Ids.New(), Mode = mode, From = new Place("") }));
    }

    /// <summary>The kind of leg a document type has: a flight ticket has flights, a hotel booking a stay.</summary>
    public static TravelMode ModeFor(DocumentType type) => type switch
    {
        DocumentType.Flight or DocumentType.BoardingPass => TravelMode.Flight,
        DocumentType.Train => TravelMode.Train,
        DocumentType.Bus => TravelMode.Bus,
        DocumentType.Hotel => TravelMode.Stay,
        DocumentType.Event => TravelMode.Event,
        _ => TravelMode.Other,
    };

    [RelayCommand]
    private void RemoveLeg(SegmentEdit leg) => Segments.Remove(leg);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_doc is not { } doc)
        {
            return;
        }

        var edited = new List<string>();
        var type = Enum.GetValues<DocumentType>()[Math.Max(0, TypeIndex)];
        Set(doc.Type != type, F.Type, () => doc.Type = type);
        Set(doc.Provider != Blank(Provider), F.Provider, () => doc.Provider = Blank(Provider));
        Set(doc.Pnr != BookingCodes.Normalize(Pnr), F.Pnr, () => doc.Pnr = BookingCodes.Normalize(Pnr));
        Set(doc.BookingReference != Blank(BookingReference), F.BookingReference, () => doc.BookingReference = Blank(BookingReference));
        doc.Notes = Blank(Notes);

        var segments = Segments.Select(s => s.ToSegment()).ToList();
        Set(!SameSegments(doc.Segments, segments), F.Segments, () => doc.Segments = segments);

        if (HasIdentity)
        {
            var identity = (doc.Identity ?? new IdentityInfo(null, null, null, null)) with { Number = Blank(IdentityNumber), ExpiresOn = DateOnly.FromDateTime(ExpiresOn) };
            Set(identity != doc.Identity, F.Identity, () => doc.Identity = identity);
        }

        if (Title.Trim() != _originalTitle && Title.Trim().Length > 0)
        {
            doc.Title = Title.Trim();
            edited.Add(F.Title);
        }

        try
        {
            await host.Services.DocumentService.UpdateAsync(doc, edited);
            await Nav.BackAsync();
        }
        catch (InvalidOperationException ex)
        {
            await dialogs.AlertAsync(AppResources.Edit_Title, ex.Message);
        }

        void Set(bool changed, string field, Action apply)
        {
            if (changed)
            {
                apply();
                edited.Add(field);
            }
        }
    }

    private static bool SameSegments(IReadOnlyList<Segment> a, IReadOnlyList<Segment> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.From == p.Second.From && p.First.To == p.Second.To && p.First.Departs == p.Second.Departs
            && p.First.Arrives == p.Second.Arrives && p.First.ServiceNumber == p.Second.ServiceNumber && p.First.Seat == p.Second.Seat);

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
