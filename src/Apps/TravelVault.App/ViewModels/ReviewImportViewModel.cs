using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.Core.Documents;
using TravelVault.Core.Duplicates;
using TravelVault.Core.Travelers;
using TravelVault.Core.Vault;

namespace TravelVault.App.ViewModels;

/// <summary>Who a printed name is: an existing traveler, a new profile, or nobody (ignore).</summary>
public sealed partial class NameChoice : ObservableObject
{
    public required string RawName { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public required IReadOnlyList<string?> TravelerIds { get; init; }

    public string Hint { get; init; } = "";

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    /// <summary>Options: travelers…, "New traveler", "Not a traveler".</summary>
    public NameDecision Decision() =>
        SelectedIndex == Options.Count - 1 ? new NameDecision(RawName, null, Ignore: true)
        : SelectedIndex == Options.Count - 2 ? new NameDecision(RawName, null, CreateNew: true)
        : new NameDecision(RawName, TravelerIds[SelectedIndex]);
}

/// <summary>
/// The review card after an import (J3): what was read (low-confidence values marked), who the printed names are,
/// and — for a likely duplicate — merge into the existing document or keep separate (J6).
/// </summary>
public sealed partial class ReviewImportViewModel(VaultHost host, ImportCoordinator importer, Dialogs dialogs) : ObservableObject
{
    private ImportDraft? _draft;
    private DuplicateCandidate? _duplicate;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Icon { get; set; } = "";

    [ObservableProperty]
    public partial string TypeText { get; set; } = "";

    [ObservableProperty]
    public partial string Warnings { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDuplicate { get; set; }

    [ObservableProperty]
    public partial string DuplicateText { get; set; } = "";

    [ObservableProperty]
    public partial bool Merge { get; set; } = true;

    [ObservableProperty]
    public partial bool NoNames { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<FieldRow> Fields { get; } = [];

    public ObservableCollection<NameChoice> Names { get; } = [];

    public async Task LoadAsync()
    {
        _draft = importer.CurrentDraft;
        if (_draft is null)
        {
            await Nav.BackAsync();
            return;
        }

        var doc = _draft.Document;
        Title = doc.Title;
        Icon = Display.Icon(doc.Type);
        TypeText = Display.TypeName(doc.Type) + (doc.Provider is null ? "" : " · " + doc.Provider);
        Warnings = string.Join("\n", _draft.Parsed.Warnings.Prepend(_draft.Parsed.NeedsReview ? AppResources.Review_Check : "").Where(w => w.Length > 0));

        Fields.Clear();
        foreach (var segment in doc.Segments)
        {
            Fields.Add(new FieldRow(Display.Route(segment), Display.When(segment.Departs) + (segment.ServiceNumber is null ? "" : " · " + segment.ServiceNumber),
                doc.ConfidenceOf(TravelDocument.Fields.Segments) == Confidence.Low));
        }

        Add(AppResources.Field_Pnr, doc.Pnr, TravelDocument.Fields.Pnr);
        Add(AppResources.Field_BookingRef, doc.BookingReference, TravelDocument.Fields.BookingReference);
        if (doc.Identity is { } id)
        {
            Add(AppResources.Field_DocumentNumber, id.MaskedNumber, TravelDocument.Fields.Identity);
            Add(AppResources.Field_Expires, id.ExpiresOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture), TravelDocument.Fields.Identity);
        }

        var travelers = await host.Services.Travelers.ListAsync(doc.FamilyId);
        Names.Clear();
        foreach (var match in _draft.Names)
        {
            var options = travelers.Select(t => t.DisplayName).ToList();
            var ids = travelers.Select(t => (string?)t.Id).ToList();
            options.Add(string.Format(AppResources.Review_NewTraveler, PersonName.Parse(match.RawName).ToTitleCase()));
            ids.Add(null);
            options.Add(AppResources.Review_NotTraveler);
            ids.Add(null);
            var selected = match.Traveler is { } t ? ids.IndexOf(t.Id) : options.Count - 2;
            Names.Add(new NameChoice
            {
                RawName = match.RawName,
                Options = options,
                TravelerIds = ids,
                SelectedIndex = selected,
                Hint = match.Traveler is not null && !match.IsConfident ? AppResources.Review_PleaseConfirm : "",
            });
        }

        NoNames = Names.Count == 0;
        _duplicate = _draft.Duplicates.FirstOrDefault();
        HasDuplicate = _duplicate is not null;
        Merge = _duplicate?.Strength == DuplicateStrength.Strong;
        DuplicateText = _duplicate is null ? "" : string.Format(
            _duplicate.Strength == DuplicateStrength.Strong ? AppResources.Review_DuplicateStrong : AppResources.Review_DuplicateWeak, _duplicate.Existing.Title);

        void Add(string label, string? value, string field)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Fields.Add(new FieldRow(label, value, doc.ConfidenceOf(field) == Confidence.Low));
            }
        }
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(edit: false);

    [RelayCommand]
    private Task SaveAndEditAsync() => SaveCoreAsync(edit: true);

    private async Task SaveCoreAsync(bool edit)
    {
        if (_draft is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var saved = await host.Services.Import.SaveAsync(_draft, [.. Names.Select(n => n.Decision())], HasDuplicate && Merge ? _duplicate!.Existing.Id : null);
            Dialogs.Toast(string.Format(AppResources.Import_Saved, saved.Title));
            importer.DraftFinished();
            await Nav.GoAsync(edit ? $"../edit?id={saved.Id}" : $"../document?id={saved.Id}");
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

    [RelayCommand]
    private async Task CancelAsync()
    {
        importer.DraftFinished();
        await Nav.BackAsync();
    }
}
