using TravelVault.App.Resources.Strings;
using TravelVault.Core.AppUpdater;

namespace TravelVault.App.Services;

/// <summary>
/// Free distribution (D37): the APK is published on GitHub Releases; this checks for a newer one (at most daily,
/// on Wi-Fi), downloads it verified against GitHub's SHA-256, and hands it to Android's installer.
/// Adapted from Use It Before's UpdateService.
/// </summary>
public sealed class UpdateService(HttpClient http, AppSettings settings, Dialogs dialogs)
{
    /// <summary>The GitHub repository the releases come from. Change this if the repository gets another name.</summary>
    public const string Owner = "SaiSanthosh97";
    public const string Repository = "travel-vault";
    public static readonly Uri RepositoryUrl = new($"https://github.com/{Owner}/{Repository}");

    private const string ApkMimeType = "application/vnd.android.package-archive";
    private readonly GitHubReleaseChecker _checker = new(http, Owner, Repository, ".apk");

    public static Version CurrentVersion => AppInfo.Current.Version;

    private static string DownloadFolder => Path.Combine(FileSystem.CacheDirectory, "updates");

    private static bool OnWifi =>
        Connectivity.Current.NetworkAccess == NetworkAccess.Internet && Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

    public async Task<AvailableUpdate?> CheckNowAsync()
    {
        var update = await _checker.CheckAsync(CurrentVersion);
        settings.LastUpdateCheck = DateTimeOffset.UtcNow;
        return update;
    }

    /// <summary>When the app opens: at most once a day, on Wi-Fi, quietly.</summary>
    public async Task OfferIfDueAsync()
    {
        if (!OnWifi || settings.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1))
        {
            return;
        }

        try
        {
            if (await CheckNowAsync() is { } update)
            {
                await OfferAsync(update);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    public async Task OfferAsync(AvailableUpdate update)
    {
        if (!await dialogs.ConfirmAsync(AppResources.Update_Title, string.Format(AppResources.Update_Available, update.Version.ToString(3)), AppResources.Update_Install, AppResources.Update_Later))
        {
            return;
        }

        if (Platform.AppContext.PackageManager?.CanRequestPackageInstalls() != true)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_AllowInstall);
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse($"package:{AppInfo.Current.PackageName}"));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(intent);
            return;
        }

        try
        {
            if (Directory.Exists(DownloadFolder))
            {
                Directory.Delete(DownloadFolder, recursive: true);
            }

            Directory.CreateDirectory(DownloadFolder);
            var path = Path.Combine(DownloadFolder, $"travel-vault-{update.Version.ToString(3)}.apk");
            await new UpdateDownloader(http).DownloadAsync(update, path);
            await Launcher.Default.OpenAsync(new OpenFileRequest(AppResources.Update_Title, new ReadOnlyFile(path, ApkMimeType)));
        }
        catch (DamagedDownloadException)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_Damaged);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_Failed);
        }
    }
}
