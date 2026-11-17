using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using TravelVault.App.Services;
using TravelVault.Device.Files;
using TravelVault.Device.Reminders;

namespace TravelVault.App;

/// <summary>
/// The only activity. Besides launching the app it is the Android share target: PDFs, images and text shared from
/// WhatsApp, Gmail, file managers or any other app arrive here ("Share → Travel Vault"), as does "Open with".
/// Shared content is read immediately (the sender's permission is temporary) and queued for import; it is
/// processed after the app is unlocked.
/// </summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "application/pdf")]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "image/*")]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault], DataMimeType = "application/pdf")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _ = HandleAsync(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        _ = HandleAsync(intent);
    }

    private static async Task HandleAsync(Intent? intent)
    {
        if (intent is null)
        {
            return;
        }

        if (intent.GetStringExtra(TravelReminders.ExtraDocumentId) is { } documentId)
        {
            intent.RemoveExtra(TravelReminders.ExtraDocumentId);
            Inbox.OpenDocument(documentId);
        }

        if (SharedContent.IsShare(intent))
        {
            var items = await SharedContent.ReadAsync(intent);
            intent.SetAction(Intent.ActionMain); // don't import the same share again after a rotation
            Inbox.AddShared(items);
        }
    }
}
