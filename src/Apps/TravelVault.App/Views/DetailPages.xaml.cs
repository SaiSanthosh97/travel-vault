using TravelVault.App.ViewModels;

namespace TravelVault.App.Views;

public partial class TripPage : VaultPage
{
    public TripPage(TripViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

public partial class DocumentPage : VaultPage
{
    public DocumentPage(DocumentViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

public partial class TravelersPage : VaultPage
{
    public TravelersPage(TravelersViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

public partial class RecentlyDeletedPage : VaultPage
{
    public RecentlyDeletedPage(RecentlyDeletedViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

/// <summary>Edit form: loaded once (reloading would throw away what the user is typing).</summary>
public partial class EditDocumentPage : ContentPage
{
    private readonly EditDocumentViewModel _viewModel;
    private bool _loaded;

    public EditDocumentPage(EditDocumentViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                await _viewModel.LoadAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // "async void": an exception here would close the app. Show a message and go back instead.
                Android.Util.Log.Error("TravelVault", $"{GetType().Name} failed to load: {ex}");
                Services.Dialogs.Toast(global::TravelVault.App.Resources.Strings.AppResources.Common_LoadFailed);
                await Services.Nav.BackAsync();
            }
        }
    }
}

public partial class ReviewImportPage : ContentPage
{
    private readonly ReviewImportViewModel _viewModel;
    private bool _loaded;

    public ReviewImportPage(ReviewImportViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                await _viewModel.LoadAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // "async void": an exception here would close the app. Show a message and leave the form instead.
                Android.Util.Log.Error("TravelVault", $"Ticket form failed to load: {ex}");
                Services.Dialogs.Toast(global::TravelVault.App.Resources.Strings.AppResources.Common_LoadFailed);
                _viewModel.CancelCommand.Execute(null);
            }
        }
    }

    // Leaving with Back is the same as Cancel: the next shared file can be processed.
    protected override bool OnBackButtonPressed()
    {
        _viewModel.CancelCommand.Execute(null);
        return true;
    }
}

public partial class ViewerPage : ContentPage
{
    private readonly ViewerViewModel _viewModel;
    private bool _loaded;

    public ViewerPage(ViewerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                await _viewModel.LoadAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // "async void": an exception here would close the app. Show a message and go back instead.
                Android.Util.Log.Error("TravelVault", $"{GetType().Name} failed to load: {ex}");
                Services.Dialogs.Toast(global::TravelVault.App.Resources.Strings.AppResources.Common_LoadFailed);
                await Services.Nav.BackAsync();
            }
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Leave();
    }
}

public partial class JoinPage : ContentPage
{
    public JoinPage(JoinViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
