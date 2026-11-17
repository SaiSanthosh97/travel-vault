using TravelVault.Device.Files;

namespace TravelVault.App.Services;

/// <summary>
/// Things Android hands the app before it may be ready (locked, starting up): shared files to import and a
/// notification's document to open. The UI takes them once the vault is unlocked.
/// </summary>
public static class Inbox
{
    private static readonly Lock Gate = new();
    private static readonly Queue<SharedItem> Shared = new();
    private static string? _documentToOpen;

    /// <summary>Raised (on any thread) when something new arrived.</summary>
    public static event Action? Arrived;

    public static void AddShared(IReadOnlyList<SharedItem> items)
    {
        lock (Gate)
        {
            foreach (var item in items)
            {
                Shared.Enqueue(item);
            }
        }

        Arrived?.Invoke();
    }

    public static void OpenDocument(string documentId)
    {
        lock (Gate)
        {
            _documentToOpen = documentId;
        }

        Arrived?.Invoke();
    }

    public static SharedItem? TakeShared()
    {
        lock (Gate)
        {
            return Shared.TryDequeue(out var item) ? item : null;
        }
    }

    public static string? TakeDocumentToOpen()
    {
        lock (Gate)
        {
            var id = _documentToOpen;
            _documentToOpen = null;
            return id;
        }
    }

    public static bool HasPending
    {
        get
        {
            lock (Gate)
            {
                return Shared.Count > 0 || _documentToOpen is not null;
            }
        }
    }
}
