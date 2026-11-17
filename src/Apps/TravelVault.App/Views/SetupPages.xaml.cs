using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.ViewModels;

namespace TravelVault.App.Views;

public partial class StartPage : ContentPage
{
    private readonly StartViewModel _viewModel;
    private bool _routed;

    public StartPage(StartViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_routed)
        {
            _routed = true;
            await _viewModel.RouteAsync();
        }
    }
}

public partial class WelcomePage : ContentPage
{
    public WelcomePage(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

public partial class RecoveryCodePage : VaultPage
{
    public RecoveryCodePage(RecoveryCodeViewModel viewModel)
    {
        InitializeComponent();
        Secure = true;
        BindingContext = viewModel;
    }

    protected override Task LoadAsync() => ((RecoveryCodeViewModel)BindingContext).LoadAsync();
}

/// <summary>Shown over everything until the user unlocks with fingerprint/face or the phone's PIN.</summary>
public sealed class LockPage : ContentPage
{
    private readonly LockService _lock;
    private readonly Func<Task> _unlocked;

    public LockPage(LockService lockService, Func<Task> unlocked)
    {
        _lock = lockService;
        _unlocked = unlocked;
        BackgroundColor = (Color)Application.Current!.Resources["Primary"];
        var button = new Button
        {
            Text = AppResources.Lock_Unlock,
            BackgroundColor = Colors.White,
            TextColor = (Color)Application.Current.Resources["Primary"],
            HorizontalOptions = LayoutOptions.Center,
            WidthRequest = 260,
        };
        button.Clicked += async (_, _) => await TryAsync();
        Content = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 24,
            Padding = 32,
            Children =
            {
                new Label { Text = "🔒", FontSize = 64, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = AppResources.AppName, FontSize = 28, TextColor = Colors.White, FontFamily = "OpenSansSemibold", HorizontalOptions = LayoutOptions.Center },
                new Label { Text = AppResources.Lock_Explain, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center },
                button,
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await TryAsync();
    }

    // The lock screen can't be dismissed with Back.
    protected override bool OnBackButtonPressed() => true;

    private async Task TryAsync()
    {
        if (await _lock.UnlockAsync())
        {
            await _unlocked();
        }
    }
}
