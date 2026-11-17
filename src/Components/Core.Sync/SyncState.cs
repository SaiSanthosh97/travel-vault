using System.Text.Json;
using TravelVault.Core.Common;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Sync;

public sealed record OutboxItem(long Id, string FamilyId, string Kind, string Payload);

/// <summary>
/// Sync bookkeeping (implemented in Storage.Sqlite, inside the encrypted database):
/// the change log holds the latest signed change per record (what this phone republishes when asked),
/// the outbox holds local changes not yet delivered to the relay.
/// </summary>
public interface ISyncStore
{
    /// <summary>Keeps the change if it is newer than the stored one for the same record.</summary>
    Task SaveChangeAsync(SignedChange change, CancellationToken cancellationToken = default);

    Task<SignedChange?> GetChangeAsync(string type, string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SignedChange>> ListChangesAsync(string familyId, CancellationToken cancellationToken = default);

    Task EnqueueAsync(string familyId, string kind, string payload, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxItem>> OutboxAsync(string familyId, CancellationToken cancellationToken = default);

    Task RemoveOutboxAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default);

    Task DeleteFamilyAsync(string familyId, CancellationToken cancellationToken = default);
}

/// <summary>Tunable numbers (tests use smaller chunks).</summary>
public sealed record SyncOptions
{
    /// <summary>ntfy.sh keeps messages ~12 h; after 11 h away, ask the family to republish.</summary>
    public TimeSpan AwayThreshold { get; init; } = TimeSpan.FromHours(11);

    public TimeSpan CheckInEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Ask again for a missing file at most this often (unless the user taps View).</summary>
    public TimeSpan FileRequestEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Send the same file again at most this often, however many phones ask.</summary>
    public TimeSpan FileResendEvery { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>After a key rotation, keep reading the old topic this long for stragglers.</summary>
    public TimeSpan OldTopicKeptFor { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Plaintext bytes per file chunk; each encrypted chunk must stay under ntfy.sh's 2 MB attachment limit.</summary>
    public int ChunkSize { get; init; } = 1_900_000;

    /// <summary>Files larger than this stay on the uploader's phone only.</summary>
    public long MaxSyncedFileSize { get; init; } = 25 * 1024 * 1024;
}

/// <summary>Typed helpers over the settings store for sync bookmarks.</summary>
internal sealed class SyncSettings(ISettingsStore settings)
{
    public async Task<T?> GetJsonAsync<T>(string key, CancellationToken ct)
    {
        var text = await settings.GetAsync(key, ct);
        return text is null ? default : JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
    }

    public Task SetJsonAsync<T>(string key, T value, CancellationToken ct) =>
        settings.SetAsync(key, JsonSerializer.Serialize(value, JsonDefaults.Options), ct);

    public async Task<DateTimeOffset?> GetTimeAsync(string key, CancellationToken ct) =>
        DateTimeOffset.TryParse(await settings.GetAsync(key, ct), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

    public Task SetTimeAsync(string key, DateTimeOffset value, CancellationToken ct) =>
        settings.SetAsync(key, value.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);

    public async Task<long> GetLongAsync(string key, CancellationToken ct) =>
        long.TryParse(await settings.GetAsync(key, ct), out var v) ? v : 0;

    public Task SetLongAsync(string key, long value, CancellationToken ct) =>
        settings.SetAsync(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

    public Task<string?> GetAsync(string key, CancellationToken ct) => settings.GetAsync(key, ct);

    public Task SetAsync(string key, string? value, CancellationToken ct) => settings.SetAsync(key, value, ct);
}

/// <summary>Setting keys, in one place.</summary>
internal static class Keys
{
    public static string LastId(string familyId, string topic) => $"family.{familyId}.lastId.{topic}";

    public static string LastSync(string familyId) => $"family.{familyId}.lastSync";

    public static string LastCheckIn(string familyId) => $"family.{familyId}.lastCheckIn";

    public static string MySeq(string familyId) => $"family.{familyId}.seq";

    public static string TheirSeq(string familyId, string memberId) => $"family.{familyId}.seq.{memberId}";

    public static string Seen(string familyId, string memberId) => $"family.{familyId}.seen.{memberId}";

    public static string Removed(string familyId) => $"family.{familyId}.removed";

    public static string OldTopics(string familyId) => $"family.{familyId}.oldTopics";

    public static string Invites(string familyId) => $"family.{familyId}.invites";

    public static string PendingJoins(string familyId) => $"family.{familyId}.pendingJoins";

    public const string OutgoingJoins = "joins.outgoing";

    public static string FileRequested(string fileId) => $"file.req.{fileId}";

    public static string FileSent(string fileId) => $"file.sent.{fileId}";
}

public sealed record OldTopic(string Topic, DateTimeOffset Until);

/// <summary>An invite this (admin) phone created and still accepts.</summary>
public sealed record StoredInvite(string InviteId, bool Remote, string? JoinTopic, byte[] JoinSecret, DateTimeOffset ExpiresAt, bool Used = false);

/// <summary>A remote join waiting for the admin to compare the safety code and approve.</summary>
public sealed record PendingJoin(string InviteId, MemberCard Member, string SafetyCode, DateTimeOffset ReceivedAt);

/// <summary>A join this phone requested and is waiting on.</summary>
public sealed record OutgoingJoin(Families.RemoteInvite Invite, DateTimeOffset RequestedAt);
