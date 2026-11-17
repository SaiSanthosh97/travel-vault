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
            await _viewModel.LoadAsync();
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
            await _viewModel.LoadAsync();
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
            await _viewModel.LoadAsync();
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
