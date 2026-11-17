using Microsoft.Extensions.Logging;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;
using TravelVault.App.ViewModels;
using TravelVault.App.Views;
using TravelVault.Device.BackgroundWork;
using TravelVault.Device.Reminders;

namespace TravelVault.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var services = builder.Services;
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<AppSettings>();
        services.AddSingleton<VaultHost>();
        services.AddSingleton<LockService>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<SyncScheduler>();
        services.AddSingleton<ImportCoordinator>();
        services.AddSingleton<FileActions>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<Dialogs>();

        // Screens (view models are transient: each visit starts fresh; the tabs are singletons).
        services.AddTransient<StartViewModel>();
        services.AddTransient<StartPage>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<WelcomePage>();
        services.AddTransient<RecoveryCodeViewModel>();
        services.AddTransient<RecoveryCodePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<TripsViewModel>();
        services.AddSingleton<TripsPage>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SearchPage>();
        services.AddSingleton<FamilyViewModel>();
        services.AddSingleton<FamilyPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddTransient<TripViewModel>();
        services.AddTransient<TripPage>();
        services.AddTransient<DocumentViewModel>();
        services.AddTransient<DocumentPage>();
        services.AddTransient<EditDocumentViewModel>();
        services.AddTransient<EditDocumentPage>();
        services.AddTransient<ReviewImportViewModel>();
        services.AddTransient<ReviewImportPage>();
        services.AddTransient<ViewerViewModel>();
        services.AddTransient<ViewerPage>();
        services.AddTransient<JoinViewModel>();
        services.AddTransient<JoinPage>();
        services.AddTransient<TravelersViewModel>();
        services.AddTransient<TravelersPage>();
        services.AddTransient<RecentlyDeletedViewModel>();
        services.AddTransient<RecentlyDeletedPage>();

        var app = builder.Build();
        FileActions.CleanUp();

        // These also run when Android starts the app without a screen (an alarm, a restart, the background job).
        var reminders = app.Services.GetRequiredService<ReminderService>();
        var host = app.Services.GetRequiredService<VaultHost>();
        TravelReminders.Initialize(
            AppResources.Reminder_Channel,
            AppResources.Reminder_ChannelDescription,
            Resource.Drawable.notification_icon,
            AppResources.Reminder_LockScreen,
            async () =>
            {
                if (await host.EnsureOpenAsync())
                {
                    await reminders.ReplanAsync();
                }
            });

        var scheduler = app.Services.GetRequiredService<SyncScheduler>();
        scheduler.Start();
        var settings = app.Services.GetRequiredService<AppSettings>();
        BackgroundWork.Initialize(token => settings.StrictMode ? Task.CompletedTask : scheduler.BackgroundAsync(token));
        return app;
    }
}
