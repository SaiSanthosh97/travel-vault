using System.Text.Json;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Relay;
using TravelVault.Core.Security;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Sync;

public sealed record SyncReport(int Received, int Sent, IReadOnlyList<string> Errors)
{
    public static readonly SyncReport Empty = new(0, 0, []);
}

/// <summary>
/// Serverless family sync. Every phone keeps the full vault; changes travel as signed, encrypted messages through a
/// relay (ntfy). One cycle per family: fetch → verify → apply → answer state/file requests → ask for what's missing →
/// flush the outbox → check in. Also the outbox writer (listens to local vault changes) and the on-demand file requester.
/// </summary>
public sealed partial class FamilySync : IVaultChangeHandler, IFileRequester
{
    internal const string DocType = "doc";
    internal const string TravelerType = "trv";
    internal const string TripType = "trip";
    internal const string FamilyType = "fam";

    private readonly VaultSession _session;
    private readonly IFamilyRepository _families;
    private readonly IDocumentRepository _documents;
    private readonly ITravelerRepository _travelers;
    private readonly ITripRepository _trips;
    private readonly DocumentService _documentService;
    private readonly TravelerService _travelerService;
    private readonly IFileKeyStore _fileKeys;
    private readonly FileVault _files;
    private readonly ISyncStore _store;
    private readonly SyncSettings _settings;
    private readonly IRelayFactory _relays;
    private readonly VaultEvents _events;
    private readonly string _incomingFolder;
    private readonly SyncOptions _options;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly SemaphoreSlim _seqGate = new(1, 1);

    public FamilySync(
        VaultSession session,
        IFamilyRepository families,
        IDocumentRepository documents,
        ITravelerRepository travelers,
        ITripRepository trips,
        DocumentService documentService,
        TravelerService travelerService,
        IFileKeyStore fileKeys,
        FileVault files,
        ISyncStore store,
        ISettingsStore settings,
        IRelayFactory relays,
        VaultEvents events,
        string incomingFolder,
        SyncOptions? options = null)
    {
        _session = session;
        _families = families;
        _documents = documents;
        _travelers = travelers;
        _trips = trips;
        _documentService = documentService;
        _travelerService = travelerService;
        _fileKeys = fileKeys;
        _files = files;
        _store = store;
        _settings = new SyncSettings(settings);
        _relays = relays;
        _events = events;
        _incomingFolder = incomingFolder;
        _options = options ?? new SyncOptions();
    }

    /// <summary>Things worth telling the user ("Asha joined", "You were removed from …").</summary>
    public event Action<string, string>? Notice;

    /// <summary>A sync step failed (network etc.); the cycle continues and retries later.</summary>
    public event Action<Exception>? Failed;

    private string Me => _session.MemberId;

    // ------------------------------------------------------------------ cycle

    /// <summary>Syncs every family on this phone, then checks joins this phone requested.</summary>
    public async Task<SyncReport> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var received = 0;
        var sent = 0;
        var errors = new List<string>();
        foreach (var family in await _families.ListAsync(cancellationToken))
        {
            var report = await SyncFamilyAsync(family.Id, cancellationToken);
            received += report.Received;
            sent += report.Sent;
            errors.AddRange(report.Errors);
        }

        try
        {
            received += await CheckOutgoingJoinsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex.Message);
            Failed?.Invoke(ex);
        }

        return new SyncReport(received, sent, errors);
    }

    public async Task<SyncReport> SyncFamilyAsync(string familyId, CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken);
        try
        {
            var family = await _families.GetAsync(familyId, cancellationToken);
            if (family is null || await IsRemovedAsync(familyId, cancellationToken))
            {
                return SyncReport.Empty;
            }

            var ctx = new Cycle(family, _relays.For(family.RelayServer));
            var lastSync = await _settings.GetTimeAsync(Keys.LastSync(familyId), cancellationToken);
            var away = lastSync is null || _session.Now - lastSync > _options.AwayThreshold;

            await Step(ctx, () => FetchAllAsync(ctx, cancellationToken));
            if (await IsRemovedAsync(familyId, cancellationToken))
            {
                return ctx.Report();
            }

            if (ctx.NeedsRotation && ctx.Family.IsAdmin(Me))
            {
                await Step(ctx, () => RotateAsync(ctx.Family, cancellationToken));
                ctx.Family = await _families.GetAsync(familyId, cancellationToken) ?? ctx.Family;
                ctx.Relay = _relays.For(ctx.Family.RelayServer);
            }

            if (ctx.Family.IsAdmin(Me))
            {
                await Step(ctx, () => CheckRemoteJoinRequestsAsync(ctx.Family, cancellationToken));
            }

            if (Admitted(ctx.Family))
            {
                await Step(ctx, () => AnswerStateRequestsAsync(ctx, cancellationToken));
                await Step(ctx, () => SendRequestedFilesAsync(ctx, cancellationToken));
                await Step(ctx, () => RequestMissingFilesAsync(ctx, cancellationToken));
                if (away || ctx.JustAdmitted)
                {
                    await Step(ctx, () => PublishAsync(ctx, Kinds.StateRequest, new StateRequestBody(Ids.New()), cancellationToken));
                }

                await Step(ctx, () => FlushOutboxAsync(ctx, cancellationToken));
                await Step(ctx, () => CheckInAsync(ctx, cancellationToken));
            }

            if (ctx.Errors.Count == 0)
            {
                await _settings.SetTimeAsync(Keys.LastSync(familyId), _session.Now, cancellationToken);
            }

            return ctx.Report();
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>Sends pending local changes now (after an edit), without a full cycle.</summary>
    public async Task FlushAsync(string familyId, CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken);
        try
        {
            if (await _families.GetAsync(familyId, cancellationToken) is { } family && Admitted(family) && !await IsRemovedAsync(familyId, cancellationToken))
            {
                var ctx = new Cycle(family, _relays.For(family.RelayServer));
                await Step(ctx, () => FlushOutboxAsync(ctx, cancellationToken));
            }
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task<DateTimeOffset?> LastSyncAsync(string familyId, CancellationToken cancellationToken = default) =>
        await _settings.GetTimeAsync(Keys.LastSync(familyId), cancellationToken);

    public async Task<DateTimeOffset?> LastSeenAsync(string familyId, string memberId, CancellationToken cancellationToken = default) =>
        await _settings.GetTimeAsync(Keys.Seen(familyId, memberId), cancellationToken);

    public async Task<int> PendingCountAsync(string familyId, CancellationToken cancellationToken = default) =>
        (await _store.OutboxAsync(familyId, cancellationToken)).Count;

    public async Task<bool> IsRemovedAsync(string familyId, CancellationToken cancellationToken = default) =>
        await _settings.GetAsync(Keys.Removed(familyId), cancellationToken) == "1";

    /// <summary>
    /// A family record received from an admin (version above zero) means this phone has been admitted.
    /// A joiner's provisional copy (from the QR code) has version zero until then.
    /// </summary>
    public static bool Admitted(Family family) => family.Version > HybridTimestamp.Zero;

    private async Task FetchAllAsync(Cycle ctx, CancellationToken ct)
    {
        var topics = new List<string> { ctx.Family.RelayTopic };
        var old = await _settings.GetJsonAsync<List<OldTopic>>(Keys.OldTopics(ctx.Family.Id), ct) ?? [];
        old = old.Where(o => o.Until > _session.Now).ToList();
        await _settings.SetJsonAsync(Keys.OldTopics(ctx.Family.Id), old, ct);
        topics.AddRange(old.Select(o => o.Topic).Where(t => t != ctx.Family.RelayTopic));

        foreach (var topic in topics)
        {
            var lastKey = Keys.LastId(ctx.Family.Id, topic);
            var messages = await ctx.Relay.FetchAsync(topic, await _settings.GetAsync(lastKey, ct), ct);
            foreach (var message in messages)
            {
                await HandleMessageAsync(ctx, topic, message, ct);
                await _settings.SetAsync(lastKey, message.Id, ct);
            }
        }
    }

    private async Task HandleMessageAsync(Cycle ctx, string topic, RelayMessage message, CancellationToken ct)
    {
        var epoch = WireCodec.EpochOf(message.Text);
        if (epoch is null || await KeyAsync(ctx.Family.Id, epoch.Value, ct) is not { } key)
        {
            return;
        }

        if (WireCodec.Decode(message.Text, ctx.Family.Id, key) is not { } decoded)
        {
            return; // not ours, damaged, or tampered with: ignore silently
        }

        var (envelope, box, signature) = decoded;
        if (envelope.From == Me)
        {
            return;
        }

        if (envelope.Kind == Kinds.Join)
        {
            await HandleJoinAsync(ctx, envelope, box, signature, ct);
            return;
        }

        var sender = ctx.Family.Find(envelope.From);
        if (sender is not { IsActive: true } || sender.SigningPublicKey.Length == 0 || !Signatures.Verify(sender.SigningPublicKey, box, signature))
        {
            return;
        }

        // Replay protection: every phone numbers its messages; an old number is a replay.
        var seqKey = Keys.TheirSeq(ctx.Family.Id, envelope.From);
        if (envelope.Seq <= await _settings.GetLongAsync(seqKey, ct))
        {
            return;
        }

        await _settings.SetLongAsync(seqKey, envelope.Seq, ct);
        await _settings.SetTimeAsync(Keys.Seen(ctx.Family.Id, envelope.From), _session.Now, ct);
        if (HybridTimestamp.TryParse(envelope.Sent, out var sent))
        {
            _session.Versions.Observe(sent);
        }

        switch (envelope.Kind)
        {
            case Kinds.Changes when envelope.BodyAs<ChangesBody>() is { } body:
                foreach (var change in body.Changes)
                {
                    await ApplyChangeAsync(ctx, change, ct);
                }

                if (body.ForRequest is not null)
                {
                    ctx.AnsweredRequests.Add(body.ForRequest);
                }

                break;
            case Kinds.StateRequest when envelope.BodyAs<StateRequestBody>() is { } request:
                ctx.PendingRequests.Add(request.RequestId);
                break;
            case Kinds.FileRequest when envelope.BodyAs<FileRequestBody>() is { } files:
                ctx.FilesAsked.UnionWith(files.Files.Where(SafeId));
                break;
            case Kinds.FileChunk when envelope.BodyAs<FileChunkBody>() is { } chunk:
                await HandleChunkAsync(ctx, chunk, message.AttachmentUrl, epoch.Value, ct);
                break;
            case Kinds.Rekey when envelope.BodyAs<RekeyBody>() is { } rekey && ctx.Family.IsAdmin(envelope.From):
                await HandleRekeyAsync(ctx, topic, sender, rekey, ct);
                break;
        }
    }

    private async Task AnswerStateRequestsAsync(Cycle ctx, CancellationToken ct)
    {
        var open = ctx.PendingRequests.Except(ctx.AnsweredRequests).ToList();
        if (open.Count == 0)
        {
            return;
        }

        // Another phone may already have answered (we saw its reply in this batch); otherwise answer once for all.
        var changes = await _store.ListChangesAsync(ctx.Family.Id, ct);
        await PublishChangesAsync(ctx, changes, open[0], ct);
    }

    private async Task FlushOutboxAsync(Cycle ctx, CancellationToken ct)
    {
        var items = await _store.OutboxAsync(ctx.Family.Id, ct);
        var changeItems = items.Where(i => i.Kind == "change").ToList();
        if (changeItems.Count > 0)
        {
            var changes = changeItems.Select(i => JsonSerializer.Deserialize<SignedChange>(i.Payload, JsonDefaults.Options)!).ToList();
            await PublishChangesAsync(ctx, changes, null, ct);
            await _store.RemoveOutboxAsync(changeItems.Select(i => i.Id), ct);
        }

        foreach (var item in items.Where(i => i.Kind == "file"))
        {
            await SendFileAsync(ctx, item.Payload, ct);
            await _store.RemoveOutboxAsync([item.Id], ct);
        }
    }

    private async Task CheckInAsync(Cycle ctx, CancellationToken ct)
    {
        var last = await _settings.GetTimeAsync(Keys.LastCheckIn(ctx.Family.Id), ct);
        if (last is null || _session.Now - last > _options.CheckInEvery)
        {
            await PublishAsync(ctx, Kinds.CheckIn, new CheckInBody(_session.DisplayName), ct);
            await _settings.SetTimeAsync(Keys.LastCheckIn(ctx.Family.Id), _session.Now, ct);
        }
    }

    // ------------------------------------------------------------------ outbox writer (local changes)

    /// <summary>
    /// Every local change is signed by this device and queued. Private documents are never sent; if one was shared
    /// before, a content-free tombstone is sent instead so the other phones drop their copy.
    /// </summary>
    public async Task HandleAsync(VaultChange change, CancellationToken cancellationToken = default)
    {
        if (change.FromSync || await _families.GetAsync(change.FamilyId, cancellationToken) is not { } family || await IsRemovedAsync(family.Id, cancellationToken))
        {
            return;
        }

        string type;
        string payload;
        HybridTimestamp version;
        switch (change.Type)
        {
            case RecordType.Document:
                if (await _documents.GetAsync(change.RecordId, cancellationToken) is not { } doc)
                {
                    return;
                }

                type = DocType;
                version = doc.Version;
                if (doc.Visibility == Visibility.Private)
                {
                    if (await _store.GetChangeAsync(DocType, doc.Id, cancellationToken) is null)
                    {
                        return; // never shared: nothing to withdraw
                    }

                    payload = Json(Withdrawn(doc));
                    break;
                }

                payload = Json(doc);
                foreach (var file in doc.Files.Where(f => f.AddedBy == Me))
                {
                    if (await _settings.GetAsync(Keys.FileSent(file.Id), cancellationToken) is null)
                    {
                        await _store.EnqueueAsync(family.Id, "file", file.Id, cancellationToken);
                        await _settings.SetTimeAsync(Keys.FileSent(file.Id), DateTimeOffset.MinValue, cancellationToken);
                    }
                }

                break;
            case RecordType.Traveler:
                if (await _travelers.GetAsync(change.RecordId, cancellationToken) is not { } traveler)
                {
                    return;
                }

                (type, payload, version) = (TravelerType, Json(traveler), traveler.Version);
                break;
            case RecordType.Trip:
                if (await _trips.GetAsync(change.RecordId, cancellationToken) is not { } trip)
                {
                    return;
                }

                (type, payload, version) = (TripType, Json(trip), trip.Version);
                break;
            case RecordType.Family:
                (type, payload, version) = (FamilyType, Json(family), family.Version);
                break;
            default:
                return;
        }

        var signed = SignedChange.Create(type, change.RecordId, family.Id, version, Me, payload, _session.DeviceKeys);
        await _store.SaveChangeAsync(signed, cancellationToken);
        await _store.EnqueueAsync(family.Id, "change", Json(signed), cancellationToken);
    }

    /// <summary>The tombstone sent for a document made private: id and version only, no details.</summary>
    private static TravelDocument Withdrawn(TravelDocument doc) => new()
    {
        Id = doc.Id,
        FamilyId = doc.FamilyId,
        OwnerMemberId = doc.OwnerMemberId,
        Type = doc.Type,
        Visibility = Visibility.Private,
        IsDeleted = true,
        DeletedAt = doc.UpdatedAt,
        Version = doc.Version,
        UpdatedBy = doc.UpdatedBy,
        UpdatedAt = doc.UpdatedAt,
    };

    // ------------------------------------------------------------------ publishing helpers

    private async Task PublishChangesAsync(Cycle ctx, IReadOnlyList<SignedChange> changes, string? forRequest, CancellationToken ct)
    {
        var key = await CurrentKeyAsync(ctx.Family, ct);
        var probe = (IReadOnlyList<SignedChange> batch) =>
            WireCodec.Fits(Envelope.Create(ctx.Family.Id, Me, 9_999_999_999, HybridTimestamp.Zero, Kinds.Changes, new ChangesBody([.. batch], forRequest)), key, _session.DeviceKeys);
        foreach (var batch in WireCodec.Batch(changes, probe))
        {
            await PublishAsync(ctx, Kinds.Changes, new ChangesBody([.. batch], forRequest), ct);
        }
    }

    private async Task PublishAsync<T>(Cycle ctx, string kind, T body, CancellationToken ct, string? topic = null, byte[]? attachment = null)
    {
        var text = await EncodeAsync(ctx.Family, kind, body, ct);
        if (attachment is null)
        {
            await ctx.Relay.PublishAsync(topic ?? ctx.Family.RelayTopic, text, ct);
        }
        else
        {
            await ctx.Relay.PublishFileAsync(topic ?? ctx.Family.RelayTopic, attachment, text, ct);
        }

        ctx.Sent++;
    }

    private async Task<string> EncodeAsync<T>(Family family, string kind, T body, CancellationToken ct, int? epoch = null, byte[]? key = null)
    {
        var envelope = Envelope.Create(family.Id, Me, await NextSeqAsync(family.Id, ct), _session.Versions.Next(), kind, body);
        return WireCodec.Encode(envelope, key ?? await CurrentKeyAsync(family, ct), epoch ?? family.KeyEpoch, _session.DeviceKeys);
    }

    private async Task<long> NextSeqAsync(string familyId, CancellationToken ct)
    {
        await _seqGate.WaitAsync(ct);
        try
        {
            var next = await _settings.GetLongAsync(Keys.MySeq(familyId), ct) + 1;
            await _settings.SetLongAsync(Keys.MySeq(familyId), next, ct);
            return next;
        }
        finally
        {
            _seqGate.Release();
        }
    }

    // ------------------------------------------------------------------ keys

    internal async Task<byte[]?> KeyAsync(string familyId, int epoch, CancellationToken ct) =>
        await _families.GetKeyAsync(familyId, epoch, ct) is { } wrapped ? _session.KeyRing.Unprotect(wrapped, KeyLabel(familyId, epoch)) : null;

    internal async Task SaveKeyAsync(string familyId, int epoch, byte[] key, CancellationToken ct) =>
        await _families.SaveKeyAsync(familyId, epoch, _session.KeyRing.Protect(key, KeyLabel(familyId, epoch)), ct);

    private async Task<byte[]> CurrentKeyAsync(Family family, CancellationToken ct) =>
        await KeyAsync(family.Id, family.KeyEpoch, ct) ?? throw new InvalidOperationException("The family key is missing on this phone.");

    private static string KeyLabel(string familyId, int epoch) => $"family/{familyId}/{epoch}";

    // ------------------------------------------------------------------ misc

    private async Task Step(Cycle ctx, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network errors, rate limits, a relay outage: keep going with the other steps; the outbox keeps
            // unsent changes and everything is retried on the next cycle.
            ctx.Errors.Add(ex.Message);
            Failed?.Invoke(ex);
        }
    }

    private void Tell(string familyId, string message) => Notice?.Invoke(familyId, message);

    internal static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    internal static T? FromJson<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static bool SafeId(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>State of one sync cycle for one family.</summary>
    private sealed class Cycle(Family family, IRelay relay)
    {
        public Family Family { get; set; } = family;

        public IRelay Relay { get; set; } = relay;

        public HashSet<string> PendingRequests { get; } = [];

        public HashSet<string> AnsweredRequests { get; } = [];

        public HashSet<string> FilesAsked { get; } = [];

        public bool JustAdmitted { get; set; }

        public bool NeedsRotation { get; set; }

        public int Received { get; set; }

        public int Sent { get; set; }

        public List<string> Errors { get; } = [];

        public SyncReport Report() => new(Received, Sent, Errors);
    }
}
