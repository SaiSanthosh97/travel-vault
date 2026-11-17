using System.Security.Cryptography;
using TravelVault.Core.Common;
using TravelVault.Core.Security;

namespace TravelVault.Core.Families;

public enum MemberRole
{
    Member = 0,
    Admin = 1,
}

/// <summary>A device (person) in a family. There are no accounts: a member is identified by its device keys.</summary>
public sealed class Member
{
    public required string MemberId { get; init; }

    public required string DisplayName { get; set; }

    public MemberRole Role { get; set; }

    public required byte[] SigningPublicKey { get; init; }

    public required byte[] AgreementPublicKey { get; init; }

    public DateTimeOffset JoinedAt { get; init; }

    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>Last time a message from this device arrived (local only, from check-ins).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public bool IsActive => RemovedAt is null;

    public DevicePublicKeys PublicKeys => new(SigningPublicKey, AgreementPublicKey);
}

/// <summary>
/// A shared vault. Invariants: at least one active admin; only admins change membership; a removed member stays
/// removed (its keys are never accepted again); removing a member moves the family to a new key epoch and topic.
/// The family key itself is not part of the aggregate — it lives encrypted in the key store.
/// </summary>
public sealed class Family : IVersioned
{
    public required string Id { get; init; }

    public string FamilyId => Id;

    public required string Name { get; set; }

    /// <summary>ntfy server, e.g. https://ntfy.sh. Configurable per family (self-hosted relays).</summary>
    public required string RelayServer { get; set; }

    /// <summary>Unguessable topic name (~190 bits). Changes on every key rotation.</summary>
    public required string RelayTopic { get; set; }

    public int KeyEpoch { get; set; }

    public List<Member> Members { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public HybridTimestamp Version { get; set; }

    public string UpdatedBy { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public const string DefaultRelay = "https://ntfy.sh";

    public static Family Create(string name, Member creator, DateTimeOffset now, string relayServer = DefaultRelay)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 60)
        {
            throw new FamilyRuleException("A family name must be 1–60 characters.");
        }

        creator.Role = MemberRole.Admin;
        return new Family
        {
            Id = Ids.New(),
            Name = trimmed,
            RelayServer = relayServer,
            RelayTopic = NewTopic(),
            KeyEpoch = 1,
            Members = [creator],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public IEnumerable<Member> ActiveMembers => Members.Where(m => m.IsActive);

    public Member? Find(string memberId) => Members.FirstOrDefault(m => m.MemberId == memberId);

    public bool IsAdmin(string memberId) => Find(memberId) is { IsActive: true, Role: MemberRole.Admin };

    public bool IsActiveMember(string memberId) => Find(memberId) is { IsActive: true };

    /// <summary>Adds a joining device. Returns false if it is already a member.</summary>
    public bool AddMember(Member member, string approvedBy)
    {
        RequireAdmin(approvedBy);
        var existing = Find(member.MemberId);
        if (existing is { IsActive: false })
        {
            throw new FamilyRuleException("A removed device can't rejoin with the same identity. It must reinstall and be invited again.");
        }

        if (existing is not null)
        {
            return false;
        }

        if (Members.Any(m => m.SigningPublicKey.AsSpan().SequenceEqual(member.SigningPublicKey)))
        {
            throw new FamilyRuleException("These device keys already belong to another member.");
        }

        member.Role = MemberRole.Member;
        Members.Add(member);
        return true;
    }

    /// <summary>
    /// Marks a member as removed (by an admin) or as having left (by themselves). The removed device keeps the key
    /// it had, so the application layer must follow every removal with <see cref="RotateKeys"/> (done by an admin's
    /// phone: on removal directly, and automatically when it sees someone leave).
    /// </summary>
    public void RemoveMember(string memberId, string removedBy, DateTimeOffset now)
    {
        var target = Find(memberId) ?? throw new FamilyRuleException("Not a member of this family.");
        if (memberId != removedBy)
        {
            RequireAdmin(removedBy);
        }

        if (!target.IsActive)
        {
            return;
        }

        if (target.Role == MemberRole.Admin && ActiveMembers.Count(m => m.Role == MemberRole.Admin) == 1 && ActiveMembers.Count() > 1)
        {
            throw new FamilyRuleException("Make someone else an admin first: a family needs at least one admin.");
        }

        target.RemovedAt = now;
    }

    /// <summary>Moves the family to a new key epoch and a new relay topic (after a removal). Admin only.</summary>
    public void RotateKeys(string rotatedBy)
    {
        RequireAdmin(rotatedBy);
        KeyEpoch++;
        RelayTopic = NewTopic();
    }

    public void SetRole(string memberId, MemberRole role, string changedBy)
    {
        RequireAdmin(changedBy);
        var target = Find(memberId) is { IsActive: true } m ? m : throw new FamilyRuleException("Not an active member.");
        if (role == MemberRole.Member && target.Role == MemberRole.Admin && ActiveMembers.Count(x => x.Role == MemberRole.Admin) == 1)
        {
            throw new FamilyRuleException("A family needs at least one admin.");
        }

        target.Role = role;
    }

    public void Rename(string name, string changedBy)
    {
        RequireAdmin(changedBy);
        var trimmed = name.Trim();
        Name = trimmed.Length is > 0 and <= 60 ? trimmed : throw new FamilyRuleException("A family name must be 1–60 characters.");
    }

    private void RequireAdmin(string memberId)
    {
        if (!IsAdmin(memberId))
        {
            throw new FamilyRuleException("Only a family admin can do this.");
        }
    }

    /// <summary>32 random base62 characters ≈ 190 bits: impossible to guess, valid as an ntfy topic.</summary>
    public static string NewTopic()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        return "tv" + RandomNumberGenerator.GetString(alphabet, 32);
    }
}

public sealed class FamilyRuleException(string message) : Exception(message);
