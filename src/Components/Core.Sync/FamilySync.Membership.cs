using System.Security.Cryptography;
using System.Text;
using TravelVault.Core.Common;
using TravelVault.Core.Families;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Sync;

public enum JoinOutcome
{
    Waiting,
    Joined,
    Rejected,
    Expired,
}

/// <summary>
/// Families and membership without a server:
/// create; invite in person (QR with the key) or remotely (text without the key + admin approval after comparing a
/// safety code); remove (with key rotation); leave.
/// </summary>
public sealed partial class FamilySync
{
    private const string JoinRequestPrefix = "tvj1.";
    private const string JoinApprovedPrefix = "tvj1ok.";
    private const string JoinRejectedPrefix = "tvj1no.";

    /// <summary>Creates a family with this phone as admin, a new key and topic, and this person's traveler profile.</summary>
    public async Task<Family> CreateFamilyAsync(string name, string relayServer = Family.DefaultRelay, CancellationToken cancellationToken = default)
    {
        var family = Family.Create(name, MeAsMember(), _session.Now, relayServer);
        await SaveKeyAsync(family.Id, family.KeyEpoch, AeadBox.NewKey(), cancellationToken);
        _session.Stamp(family);
        await _families.SaveAsync(family, cancellationToken);
        await _events.PublishAsync(new VaultChange(family.Id, RecordType.Family, family.Id), cancellationToken);
        await _travelerService.MyTravelerAsync(family.Id, cancellationToken);
        return family;
    }

    // ------------------------------------------------------------------ in person (QR)

    public async Task<InPersonInvite> CreateInPersonInviteAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var family = await RequireAdminFamilyAsync(familyId, cancellationToken);
        var invite = new InPersonInvite(
            Ids.New(), InviteCodec.NewJoinSecret(), family.Id, family.Name, family.RelayServer, family.RelayTopic, family.KeyEpoch,
            await CurrentKeyAsync(family, cancellationToken), Me, _session.DeviceKeys.Public.Signing, _session.Now + InviteCodec.InPersonLifetime);
        await AddStoredInviteAsync(family.Id, new StoredInvite(invite.InviteId, false, null, invite.JoinSecret, invite.ExpiresAt), cancellationToken);
        return invite;
    }

    /// <summary>
    /// Joins from a scanned QR code. This phone stores a provisional copy of the family (just the inviting admin)
    /// and announces itself; the admin's phone checks the one-time invite and admits it, and the family record
    /// it then publishes completes the join on the next sync.
    /// </summary>
    public async Task<Family> JoinInPersonAsync(InPersonInvite invite, CancellationToken cancellationToken = default)
    {
        if (await _families.GetAsync(invite.FamilyId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var provisional = new Family
        {
            Id = invite.FamilyId,
            Name = invite.FamilyName,
            RelayServer = invite.RelayServer,
            RelayTopic = invite.RelayTopic,
            KeyEpoch = invite.KeyEpoch,
            CreatedAt = _session.Now,
            Members =
            [
                new Member { MemberId = invite.InviterMemberId, DisplayName = "Admin", Role = MemberRole.Admin, SigningPublicKey = invite.InviterSigningKey, AgreementPublicKey = [] },
                MeAsMember(),
            ],
        };

        await SaveKeyAsync(provisional.Id, provisional.KeyEpoch, invite.FamilyKey, cancellationToken);
        await _families.SaveAsync(provisional, cancellationToken); // version zero = not admitted yet
        var card = MemberCard.Of(Me, _session.DisplayName, _session.DeviceKeys.Public);
        var ctx = new Cycle(provisional, _relays.For(provisional.RelayServer));
        await PublishAsync(ctx, Kinds.Join, new JoinBody(card, invite.InviteId, Proof(invite.JoinSecret, card)), cancellationToken);
        return provisional;
    }

    private async Task HandleJoinAsync(Cycle ctx, Envelope envelope, byte[] box, byte[] signature, CancellationToken ct)
    {
        if (envelope.BodyAs<JoinBody>() is not { } body || body.Member.MemberId != envelope.From || !ctx.Family.IsAdmin(Me))
        {
            return;
        }

        var keys = body.Member.Keys;
        if (!Signatures.Verify(keys.Signing, box, signature))
        {
            return;
        }

        var invites = await StoredInvitesAsync(ctx.Family.Id, ct);
        var invite = invites.FirstOrDefault(i => i.InviteId == body.InviteId && !i.Remote && !i.Used && i.ExpiresAt > _session.Now);
        if (invite is null || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(body.Proof), Convert.FromBase64String(Proof(invite.JoinSecret, body.Member))))
        {
            return;
        }

        await AdmitAsync(ctx.Family, body.Member, invite, ct);
        ctx.Family = await _families.GetAsync(ctx.Family.Id, ct) ?? ctx.Family;
    }

    // ------------------------------------------------------------------ remote (text + approval)

    public async Task<RemoteInvite> CreateRemoteInviteAsync(string familyId, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        var family = await RequireAdminFamilyAsync(familyId, cancellationToken);
        var invite = new RemoteInvite(
            Ids.New(), family.Id, family.Name, family.RelayServer, Family.NewTopic(), InviteCodec.NewJoinSecret(),
            Me, _session.DeviceKeys.Public.Signing, _session.DeviceKeys.Public.Agreement, _session.Now + (lifetime ?? InviteCodec.RemoteDefaultLifetime));
        await AddStoredInviteAsync(family.Id, new StoredInvite(invite.InviteId, true, invite.JoinTopic, invite.JoinSecret, invite.ExpiresAt), cancellationToken);
        return invite;
    }

    /// <summary>
    /// Sends this phone's keys to the admin (sealed so only the admin can read them).
    /// Returns the safety code to read out to the admin, who sees the same code before approving.
    /// </summary>
    public async Task<string> RequestRemoteJoinAsync(RemoteInvite invite, CancellationToken cancellationToken = default)
    {
        var card = MemberCard.Of(Me, _session.DisplayName, _session.DeviceKeys.Public);
        var request = new JoinRequest(invite.InviteId, card, Proof(invite.JoinSecret, card));
        var sealedRequest = RecipientBox.Seal(invite.AdminAgreementKey, Encoding.UTF8.GetBytes(Json(request)), "join/" + invite.InviteId);
        await _relays.For(invite.RelayServer).PublishAsync(invite.JoinTopic, JoinRequestPrefix + WireCodec.B64(sealedRequest), cancellationToken);

        var outgoing = await _settings.GetJsonAsync<List<OutgoingJoin>>(Keys.OutgoingJoins, cancellationToken) ?? [];
        outgoing.RemoveAll(o => o.Invite.InviteId == invite.InviteId);
        outgoing.Add(new OutgoingJoin(invite, _session.Now));
        await _settings.SetJsonAsync(Keys.OutgoingJoins, outgoing, cancellationToken);
        return SafetyCode.Compute(new DevicePublicKeys(invite.AdminSigningKey, invite.AdminAgreementKey), _session.DeviceKeys.Public, invite.JoinSecret);
    }

    /// <summary>Admin side: reads join requests sent to this family's open remote invites.</summary>
    private async Task CheckRemoteJoinRequestsAsync(Family family, CancellationToken ct)
    {
        var invites = (await StoredInvitesAsync(family.Id, ct)).Where(i => i.Remote && !i.Used && i.ExpiresAt > _session.Now && i.JoinTopic is not null).ToList();
        if (invites.Count == 0)
        {
            return;
        }

        var relay = _relays.For(family.RelayServer);
        var pending = await PendingJoinsAsync(family.Id, ct);
        foreach (var invite in invites)
        {
            foreach (var message in await relay.FetchAsync(invite.JoinTopic!, null, ct))
            {
                if (!message.Text.StartsWith(JoinRequestPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                byte[]? opened;
                try
                {
                    opened = _session.DeviceKeys.OpenSealed(WireCodec.FromB64(message.Text[JoinRequestPrefix.Length..]), "join/" + invite.InviteId);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (opened is null || FromJson<JoinRequest>(Encoding.UTF8.GetString(opened)) is not { } request || request.InviteId != invite.InviteId
                    || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(request.Proof), Convert.FromBase64String(Proof(invite.JoinSecret, request.Member)))
                    || pending.Any(p => p.Member.MemberId == request.Member.MemberId) || family.Find(request.Member.MemberId) is not null)
                {
                    continue;
                }

                var code = SafetyCode.Compute(_session.DeviceKeys.Public, request.Member.Keys, invite.JoinSecret);
                pending.Add(new PendingJoin(invite.InviteId, request.Member, code, _session.Now));
                Tell(family.Id, $"{request.Member.DisplayName} wants to join {family.Name}. Compare the code {code} before approving.");
            }
        }

        await _settings.SetJsonAsync(Keys.PendingJoins(family.Id), pending, ct);
    }

    public async Task<List<PendingJoin>> PendingJoinsAsync(string familyId, CancellationToken cancellationToken = default) =>
        await _settings.GetJsonAsync<List<PendingJoin>>(Keys.PendingJoins(familyId), cancellationToken) ?? [];

    /// <summary>Admin approves after comparing the safety code: the member is added and receives the family key.</summary>
    public async Task ApproveJoinAsync(string familyId, string memberId, CancellationToken cancellationToken = default)
    {
        var family = await RequireAdminFamilyAsync(familyId, cancellationToken);
        var pending = await PendingJoinsAsync(familyId, cancellationToken);
        var join = pending.FirstOrDefault(p => p.Member.MemberId == memberId) ?? throw new InvalidOperationException("No such join request.");
        var invite = (await StoredInvitesAsync(familyId, cancellationToken)).FirstOrDefault(i => i.InviteId == join.InviteId && !i.Used)
            ?? throw new InvalidOperationException("The invite was already used or has expired.");

        await AdmitAsync(family, join.Member, invite, cancellationToken);
        family = (await _families.GetAsync(familyId, cancellationToken))!;

        var familyChange = await _store.GetChangeAsync(FamilyType, family.Id, cancellationToken);
        var approval = new JoinApproval(Json(family), family.KeyEpoch, Convert.ToBase64String(await CurrentKeyAsync(family, cancellationToken)), familyChange);
        var box = RecipientBox.Seal(join.Member.Keys.Agreement, Encoding.UTF8.GetBytes(Json(approval)), "join-ok/" + invite.InviteId);
        await _relays.For(family.RelayServer).PublishAsync(invite.JoinTopic!, $"{JoinApprovedPrefix}{WireCodec.B64(box)}.{WireCodec.B64(_session.DeviceKeys.Sign(box))}", cancellationToken);

        pending.RemoveAll(p => p.Member.MemberId == memberId);
        await _settings.SetJsonAsync(Keys.PendingJoins(familyId), pending, cancellationToken);
        await FlushAsync(familyId, cancellationToken);
    }

    public async Task RejectJoinAsync(string familyId, string memberId, CancellationToken cancellationToken = default)
    {
        var family = await RequireAdminFamilyAsync(familyId, cancellationToken);
        var pending = await PendingJoinsAsync(familyId, cancellationToken);
        if (pending.FirstOrDefault(p => p.Member.MemberId == memberId) is not { } join)
        {
            return;
        }

        var invites = await StoredInvitesAsync(familyId, cancellationToken);
        if (invites.FirstOrDefault(i => i.InviteId == join.InviteId) is { JoinTopic: { } topic })
        {
            var signature = _session.DeviceKeys.Sign(Encoding.UTF8.GetBytes($"reject|{join.InviteId}|{memberId}"));
            await _relays.For(family.RelayServer).PublishAsync(topic, $"{JoinRejectedPrefix}{memberId}.{WireCodec.B64(signature)}", cancellationToken);
            await SetInvitesAsync(familyId, invites.Select(i => i.InviteId == join.InviteId ? i with { Used = true } : i).ToList(), cancellationToken);
        }

        pending.RemoveAll(p => p.Member.MemberId == memberId);
        await _settings.SetJsonAsync(Keys.PendingJoins(familyId), pending, cancellationToken);
    }

    /// <summary>Joiner side: looks for the admin's answer on each invite this phone used.</summary>
    public async Task<JoinOutcome> CheckJoinAsync(string inviteId, CancellationToken cancellationToken = default)
    {
        var outgoing = await _settings.GetJsonAsync<List<OutgoingJoin>>(Keys.OutgoingJoins, cancellationToken) ?? [];
        if (outgoing.FirstOrDefault(o => o.Invite.InviteId == inviteId) is not { } join)
        {
            return await _families.GetAsync(inviteId, cancellationToken) is null ? JoinOutcome.Expired : JoinOutcome.Joined;
        }

        var outcome = await CheckOneJoinAsync(join, cancellationToken);
        if (outcome != JoinOutcome.Waiting)
        {
            outgoing.RemoveAll(o => o.Invite.InviteId == inviteId);
            await _settings.SetJsonAsync(Keys.OutgoingJoins, outgoing, cancellationToken);
        }

        return outcome;
    }

    private async Task<int> CheckOutgoingJoinsAsync(CancellationToken ct)
    {
        var outgoing = await _settings.GetJsonAsync<List<OutgoingJoin>>(Keys.OutgoingJoins, ct) ?? [];
        var joined = 0;
        foreach (var join in outgoing.ToList())
        {
            if (await CheckJoinAsync(join.Invite.InviteId, ct) == JoinOutcome.Joined)
            {
                joined++;
            }
        }

        return joined;
    }

    private async Task<JoinOutcome> CheckOneJoinAsync(OutgoingJoin join, CancellationToken ct)
    {
        var invite = join.Invite;
        foreach (var message in await _relays.For(invite.RelayServer).FetchAsync(invite.JoinTopic, null, ct))
        {
            if (message.Text.StartsWith(JoinRejectedPrefix, StringComparison.Ordinal))
            {
                var parts = message.Text[JoinRejectedPrefix.Length..].Split('.');
                if (parts.Length == 2 && parts[0] == Me
                    && Signatures.Verify(invite.AdminSigningKey, Encoding.UTF8.GetBytes($"reject|{invite.InviteId}|{Me}"), WireCodec.FromB64(parts[1])))
                {
                    return JoinOutcome.Rejected;
                }

                continue;
            }

            if (!message.Text.StartsWith(JoinApprovedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var body = message.Text[JoinApprovedPrefix.Length..].Split('.');
            if (body.Length != 2)
            {
                continue;
            }

            var box = WireCodec.FromB64(body[0]);
            if (!Signatures.Verify(invite.AdminSigningKey, box, WireCodec.FromB64(body[1]))
                || _session.DeviceKeys.OpenSealed(box, "join-ok/" + invite.InviteId) is not { } opened
                || FromJson<JoinApproval>(Encoding.UTF8.GetString(opened)) is not { } approval
                || FromJson<Family>(approval.Family) is not { } family
                || family.Id != invite.FamilyId || !family.IsActiveMember(Me))
            {
                continue;
            }

            await SaveKeyAsync(family.Id, approval.Epoch, Convert.FromBase64String(approval.Key), ct);
            await _families.SaveAsync(family, ct);
            if (approval.FamilyChange is { } change)
            {
                await _store.SaveChangeAsync(change, ct);
            }

            await _travelerService.MyTravelerAsync(family.Id, ct);
            Tell(family.Id, $"You joined {family.Name}");
            await _events.PublishAsync(new VaultChange(family.Id, RecordType.Family, family.Id, FromSync: true), ct);
            return JoinOutcome.Joined;
        }

        return _session.Now > invite.ExpiresAt + TimeSpan.FromDays(1) ? JoinOutcome.Expired : JoinOutcome.Waiting;
    }

    // ------------------------------------------------------------------ removal, rotation, leaving

    /// <summary>Admin removes a member: new key + topic, sealed to each remaining member; the removed phone gets nothing new.</summary>
    public async Task RemoveMemberAsync(string familyId, string memberId, CancellationToken cancellationToken = default)
    {
        var family = await RequireAdminFamilyAsync(familyId, cancellationToken);
        family.RemoveMember(memberId, Me, _session.Now);
        await RotateAsync(family, cancellationToken);
    }

    /// <summary>
    /// Key rotation: epoch+1 with a fresh random key and topic. The new key is sealed separately to every remaining
    /// member's device key and announced on the OLD topic (where everyone still listens); members switch over.
    /// The removed device can read the announcement's envelope but not the key inside it.
    /// </summary>
    private async Task RotateAsync(Family family, CancellationToken ct)
    {
        var oldTopic = family.RelayTopic;
        var oldEpoch = family.KeyEpoch;
        var oldKey = await CurrentKeyAsync(family, ct);

        family.RotateKeys(Me);
        var newKey = AeadBox.NewKey();
        await SaveKeyAsync(family.Id, family.KeyEpoch, newKey, ct);
        _session.Stamp(family);
        await _families.SaveAsync(family, ct);

        var familyChange = SignedChange.Create(FamilyType, family.Id, family.Id, family.Version, Me, Json(family), _session.DeviceKeys);
        await _store.SaveChangeAsync(familyChange, ct);

        var sealedKeys = family.ActiveMembers
            .Where(m => m.MemberId != Me && m.AgreementPublicKey.Length > 0)
            .ToDictionary(m => m.MemberId, m => Convert.ToBase64String(RecipientBox.Seal(m.AgreementPublicKey, newKey, RekeyLabel(family.Id, family.KeyEpoch))));
        var relay = _relays.For(family.RelayServer);
        var announcement = await EncodeAsync(family, Kinds.Rekey, new RekeyBody(family.KeyEpoch, family.RelayTopic, sealedKeys, familyChange), ct, oldEpoch, oldKey);
        await relay.PublishAsync(oldTopic, announcement, ct);

        await RememberOldTopicAsync(family.Id, oldTopic, ct);
        await _events.PublishAsync(new VaultChange(family.Id, RecordType.Family, family.Id, FromSync: true), ct);
    }

    private async Task HandleRekeyAsync(Cycle ctx, string topic, Member sender, RekeyBody rekey, CancellationToken ct)
    {
        if (rekey.Epoch <= ctx.Family.KeyEpoch || !rekey.Family.Verify(sender.SigningPublicKey))
        {
            return;
        }

        if (rekey.Keys.TryGetValue(Me, out var sealedKey)
            && _session.DeviceKeys.OpenSealed(Convert.FromBase64String(sealedKey), RekeyLabel(ctx.Family.Id, rekey.Epoch)) is { } newKey)
        {
            await SaveKeyAsync(ctx.Family.Id, rekey.Epoch, newKey, ct);
            await ApplyChangeAsync(ctx, rekey.Family, ct);
            await RememberOldTopicAsync(ctx.Family.Id, topic, ct);
            return;
        }

        // No key for us: if the signed family record shows us removed, say so and stop syncing this family.
        if (FromJson<Family>(rekey.Family.Payload) is { } family && family.Id == ctx.Family.Id && family.Find(Me) is { IsActive: false })
        {
            await MarkRemovedAsync(family, ct);
        }
    }

    /// <summary>Leaves a family: tells the others, then deletes this family's data and keys from this phone.</summary>
    public async Task LeaveFamilyAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var family = await _families.GetAsync(familyId, cancellationToken) ?? throw new InvalidOperationException("Not a member.");
        if (Admitted(family) && family.ActiveMembers.Count() > 1 && !await IsRemovedAsync(familyId, cancellationToken))
        {
            family.RemoveMember(Me, Me, _session.Now); // throws if this is the last admin
            _session.Stamp(family);
            var change = SignedChange.Create(FamilyType, family.Id, family.Id, family.Version, Me, Json(family), _session.DeviceKeys);
            var ctx = new Cycle(family, _relays.For(family.RelayServer));
            await PublishAsync(ctx, Kinds.Changes, new ChangesBody([change]), cancellationToken);
        }

        await DeleteFamilyDataAsync(familyId, cancellationToken);
    }

    /// <summary>Removes everything about a family from this phone (after leaving or being removed).</summary>
    public async Task DeleteFamilyDataAsync(string familyId, CancellationToken cancellationToken = default)
    {
        await _families.DeleteEverythingAsync(familyId, cancellationToken);
        await _store.DeleteFamilyAsync(familyId, cancellationToken);
        _files.DeleteFamily(familyId);
        var incoming = Path.Combine(_incomingFolder, familyId);
        if (Directory.Exists(incoming))
        {
            Directory.Delete(incoming, recursive: true);
        }

        await _events.PublishAsync(new VaultChange(familyId, RecordType.Family, familyId, FromSync: true), cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private async Task AdmitAsync(Family family, MemberCard card, StoredInvite invite, CancellationToken ct)
    {
        var keys = card.Keys;
        var added = family.AddMember(
            new Member
            {
                MemberId = card.MemberId,
                DisplayName = string.IsNullOrWhiteSpace(card.DisplayName) ? "Family member" : card.DisplayName.Trim(),
                SigningPublicKey = keys.Signing,
                AgreementPublicKey = keys.Agreement,
                JoinedAt = _session.Now,
            },
            Me);

        var invites = await StoredInvitesAsync(family.Id, ct);
        await SetInvitesAsync(family.Id, invites.Select(i => i.InviteId == invite.InviteId ? i with { Used = true } : i).ToList(), ct);
        if (!added)
        {
            return;
        }

        _session.Stamp(family);
        await _families.SaveAsync(family, ct);
        await _events.PublishAsync(new VaultChange(family.Id, RecordType.Family, family.Id), ct); // signs + queues the family record
        Tell(family.Id, $"{card.DisplayName} joined {family.Name}");
    }

    private async Task<Family> RequireAdminFamilyAsync(string familyId, CancellationToken ct)
    {
        var family = await _families.GetAsync(familyId, ct) ?? throw new InvalidOperationException("Not a member of this family.");
        return family.IsAdmin(Me) ? family : throw new FamilyRuleException("Only a family admin can do this.");
    }

    private Member MeAsMember() => new()
    {
        MemberId = Me,
        DisplayName = _session.DisplayName,
        SigningPublicKey = _session.DeviceKeys.Public.Signing,
        AgreementPublicKey = _session.DeviceKeys.Public.Agreement,
        JoinedAt = _session.Now,
    };

    private async Task<List<StoredInvite>> StoredInvitesAsync(string familyId, CancellationToken ct) =>
        await _settings.GetJsonAsync<List<StoredInvite>>(Keys.Invites(familyId), ct) ?? [];

    private async Task AddStoredInviteAsync(string familyId, StoredInvite invite, CancellationToken ct)
    {
        var invites = (await StoredInvitesAsync(familyId, ct)).Where(i => i.ExpiresAt > _session.Now - TimeSpan.FromDays(7)).ToList();
        invites.Add(invite);
        await SetInvitesAsync(familyId, invites, ct);
    }

    private Task SetInvitesAsync(string familyId, List<StoredInvite> invites, CancellationToken ct) => _settings.SetJsonAsync(Keys.Invites(familyId), invites, ct);

    private async Task RememberOldTopicAsync(string familyId, string topic, CancellationToken ct)
    {
        var old = await _settings.GetJsonAsync<List<OldTopic>>(Keys.OldTopics(familyId), ct) ?? [];
        old.RemoveAll(o => o.Topic == topic);
        old.Add(new OldTopic(topic, _session.Now + _options.OldTopicKeptFor));
        await _settings.SetJsonAsync(Keys.OldTopics(familyId), old, ct);
    }

    /// <summary>Proof of holding the invite secret, bound to the joiner's keys (HMAC-SHA256).</summary>
    private static string Proof(byte[] joinSecret, MemberCard card) =>
        Convert.ToBase64String(HMACSHA256.HashData(joinSecret, Encoding.UTF8.GetBytes($"{card.MemberId}|{card.SigningKey}|{card.AgreementKey}")));

    private static string RekeyLabel(string familyId, int epoch) => $"rekey/{familyId}/{epoch}";

    private sealed record JoinRequest(string InviteId, MemberCard Member, string Proof);

    private sealed record JoinApproval(string Family, int Epoch, string Key, SignedChange? FamilyChange);
}
