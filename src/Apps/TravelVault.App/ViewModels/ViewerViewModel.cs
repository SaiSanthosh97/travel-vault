using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;

using Visibility = TravelVault.Core.Documents.Visibility;

namespace TravelVault.App.ViewModels;

public sealed record PageImage(ImageSource Source);

/// <summary>
/// The in-app viewer (FR-A1): the decrypted file exists only in memory. PDFs are rendered to page images on the
/// phone, images shown as they are, shared text as text. Barcode mode (FR-B3) turns brightness up and keeps the
/// screen on for gate scanners. If the file isn't on this phone yet, family phones are asked for it.
/// </summary>
public sealed partial class ViewerViewModel(VaultHost host, AppSettings settings, FileActions files) : ObservableObject, IQueryAttributable
{
    private string _id = "";
    private string? _fileId;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool Missing { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool BarcodeMode { get; set; }

    public ObservableCollection<PageImage> Pages { get; } = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _id = query["id"]?.ToString() ?? "";
        _fileId = query.TryGetValue("file", out var f) ? f?.ToString() : null;
        BarcodeMode = query.ContainsKey("barcode");
    }

    partial void OnBarcodeModeChanged(bool value) => Services.Activity.SetBarcodeMode(value);

    public async Task LoadAsync()
    {
        if (await host.Services.Documents.GetAsync(_id) is not { } doc)
        {
            return;
        }

        Title = doc.Title;
        Services.Activity.SetSecure(doc.Visibility == Visibility.Private || doc.Type is DocumentType.Passport or DocumentType.Visa, settings);
        Services.Activity.SetBarcodeMode(BarcodeMode);
        IsBusy = true;
        try
        {
            if (await files.OpenAsync(doc, _fileId) is not { } file)
            {
                Missing = true;
                return;
            }

            Missing = false;
            Pages.Clear();
            Text = "";
            switch (file.File.MediaType)
            {
                case MediaTypes.Pdf:
                    foreach (var page in await host.Services.Rasterizer.RenderPagesAsync(file.Content, 20))
                    {
                        Pages.Add(new PageImage(ImageSource.FromStream(() => new MemoryStream(page))));
                    }

                    break;
                case MediaTypes.Text:
                    Text = System.Text.Encoding.UTF8.GetString(file.Content);
                    break;
                default:
                    var bytes = file.Content;
                    Pages.Add(new PageImage(ImageSource.FromStream(() => new MemoryStream(bytes))));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Text = AppResources.Viewer_CantShow;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Leave() => Services.Activity.SetBarcodeMode(false);

    [RelayCommand]
    private void ToggleBarcode() => BarcodeMode = !BarcodeMode;

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();
}
