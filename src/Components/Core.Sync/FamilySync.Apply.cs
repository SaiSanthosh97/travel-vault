using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Sync;

public sealed partial class FamilySync
{
    /// <summary>
    /// Applies one signed change from another phone. Checks, in order: right family; known author whose signature
    /// verifies; not written after the author was removed; newer than what we have. Conflicts: last writer wins by
    /// hybrid timestamp (documents keep local user edits, see DocumentConflicts).
    /// </summary>
    private async Task ApplyChangeAsync(Cycle ctx, SignedChange change, CancellationToken ct)
    {
        if (change.FamilyId != ctx.Family.Id || !SafeId(change.Id))
        {
            return;
        }

        var author = ctx.Family.Find(change.Author);
        if (author is null || author.SigningPublicKey.Length == 0 || !change.Verify(author.SigningPublicKey))
        {
            return;
        }

        if (author.RemovedAt is { } removed && change.VersionValue.WallMs > removed.ToUnixTimeMilliseconds())
        {
            return; // written after removal: not accepted
        }

        if (await _store.GetChangeAsync(change.Type, change.Id, ct) is { } known && change.VersionValue <= known.VersionValue)
        {
            return; // already have this or something newer
        }

        _session.Versions.Observe(change.VersionValue);
        var applied = change.Type switch
        {
            DocType => await ApplyDocumentAsync(ctx, change, ct),
            TravelerType => await ApplyTravelerAsync(ctx, change, ct),
            TripType => await ApplyTripAsync(ctx, change, ct),
            FamilyType => await ApplyFamilyAsync(ctx, change, ct),
            _ => false,
        };

        if (!applied)
        {
            return;
        }

        await _store.SaveChangeAsync(change, ct);
        ctx.Received++;
        var recordType = change.Type switch
        {
            DocType => RecordType.Document,
            TravelerType => RecordType.Traveler,
            TripType => RecordType.Trip,
            _ => RecordType.Family,
        };
        await _events.PublishAsync(new VaultChange(ctx.Family.Id, recordType, change.Id, FromSync: true), ct);
    }

    private async Task<bool> ApplyDocumentAsync(Cycle ctx, SignedChange change, CancellationToken ct)
    {
        var incoming = FromJson<TravelDocument>(change.Payload);
        if (incoming is null || incoming.Id != change.Id || incoming.FamilyId != ctx.Family.Id)
        {
            return false;
        }

        var local = await _documents.GetAsync(incoming.Id, ct);
        if (local is not null && (local.FamilyId != ctx.Family.Id || incoming.Version <= local.Version))
        {
            return false;
        }

        if (local is { Visibility: Visibility.Private } && local.OwnerMemberId == Me)
        {
            return false; // my private document: nobody else may change it
        }

        await _documents.SaveAsync(local is null ? incoming : DocumentConflicts.Resolve(local, incoming), ct);
        return true;
    }

    private async Task<bool> ApplyTravelerAsync(Cycle ctx, SignedChange change, CancellationToken ct)
    {
        var incoming = FromJson<Traveler>(change.Payload);
        if (incoming is null || incoming.Id != change.Id || incoming.FamilyId != ctx.Family.Id)
        {
            return false;
        }

        var local = await _travelers.GetAsync(incoming.Id, ct);
        if (local is not null && (local.FamilyId != ctx.Family.Id || !Versioning.Wins(incoming, local)))
        {
            return false;
        }

        await _travelers.SaveAsync(incoming, ct);
        return true;
    }

    private async Task<bool> ApplyTripAsync(Cycle ctx, SignedChange change, CancellationToken ct)
    {
        var incoming = FromJson<Trip>(change.Payload);
        if (incoming is null || incoming.Id != change.Id || incoming.FamilyId != ctx.Family.Id)
        {
            return false;
        }

        var local = await _trips.GetAsync(incoming.Id, ct);
        if (local is not null && (local.FamilyId != ctx.Family.Id || !Versioning.Wins(incoming, local)))
        {
            return false;
        }

        await _trips.SaveAsync(incoming, ct);
        return true;
    }

    /// <summary>
    /// Membership changes have the strictest rules: written by an active admin — or a member leaving (only their own
    /// "removed" mark changes); existing members' keys can never change (key pinning: a forged "same member, new key"
    /// is refused); a new key epoch is only accepted once this phone holds that epoch's key (delivered by rekey).
    /// </summary>
    private async Task<bool> ApplyFamilyAsync(Cycle ctx, SignedChange change, CancellationToken ct)
    {
        var incoming = FromJson<Family>(change.Payload);
        var local = ctx.Family;
        if (incoming is null || incoming.Id != local.Id || incoming.Version <= local.Version)
        {
            return false;
        }

        var byAdmin = local.IsAdmin(change.Author);
        if (!byAdmin && !IsSelfLeave(local, incoming, change.Author))
        {
            return false;
        }

        foreach (var member in local.Members.Where(m => m.SigningPublicKey.Length > 0))
        {
            if (incoming.Find(member.MemberId) is not { } same
                || !same.SigningPublicKey.AsSpan().SequenceEqual(member.SigningPublicKey)
                || (member.AgreementPublicKey.Length > 0 && !same.AgreementPublicKey.AsSpan().SequenceEqual(member.AgreementPublicKey)))
            {
                return false;
            }
        }

        if (incoming.KeyEpoch < local.KeyEpoch || (incoming.KeyEpoch != local.KeyEpoch && await KeyAsync(incoming.Id, incoming.KeyEpoch, ct) is null))
        {
            return false;
        }

        var wasAdmitted = Admitted(local);
        await _families.SaveAsync(incoming, ct);
        ctx.Family = incoming;
        ctx.Relay = _relays.For(incoming.RelayServer);

        foreach (var added in incoming.ActiveMembers.Where(m => local.Find(m.MemberId) is null && m.MemberId != Me))
        {
            Tell(local.Id, $"{added.DisplayName} joined {incoming.Name}");
        }

        if (incoming.Find(Me) is { IsActive: false })
        {
            await MarkRemovedAsync(incoming, ct);
            return true;
        }

        if (!wasAdmitted && incoming.IsActiveMember(Me))
        {
            ctx.JustAdmitted = true;
            Tell(local.Id, $"You joined {incoming.Name}");
        }

        // Someone left on their own: they still hold the key, so the lead admin rotates it.
        var leavers = incoming.Members.Where(m => !m.IsActive && local.Find(m.MemberId) is { IsActive: true }).ToList();
        if (leavers.Count > 0 && LeadAdmin(incoming) == Me)
        {
            ctx.NeedsRotation = true;
        }

        return true;
    }

    private static bool IsSelfLeave(Family local, Family incoming, string author)
    {
        if (local.Find(author) is not { IsActive: true } || incoming.Find(author) is not { IsActive: false })
        {
            return false;
        }

        // Nothing else may change: same name, relay, epoch, and the same other members with the same roles.
        if (incoming.Name != local.Name || incoming.RelayTopic != local.RelayTopic || incoming.KeyEpoch != local.KeyEpoch || incoming.Members.Count != local.Members.Count)
        {
            return false;
        }

        return local.Members.Where(m => m.MemberId != author).All(m =>
            incoming.Find(m.MemberId) is { } x && x.Role == m.Role && x.RemovedAt == m.RemovedAt);
    }

    /// <summary>One admin acts for automatic tasks so two admins don't rotate at once: the lowest member id.</summary>
    private static string? LeadAdmin(Family family) =>
        family.ActiveMembers.Where(m => m.Role == MemberRole.Admin).Select(m => m.MemberId).Order(StringComparer.Ordinal).FirstOrDefault();

    private async Task MarkRemovedAsync(Family family, CancellationToken ct)
    {
        await _settings.SetAsync(Keys.Removed(family.Id), "1", ct);
        Tell(family.Id, $"You were removed from {family.Name}. Documents already on this phone stay until you delete them.");
        await _events.PublishAsync(new VaultChange(family.Id, RecordType.Family, family.Id, FromSync: true), ct);
    }
}
