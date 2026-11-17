using TravelVault.Core.Families;
using TravelVault.Core.Security;

namespace TravelVault.Core.Families.Tests;

public class FamilyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static Member NewMember(string id, string name)
    {
        using var keys = DeviceKeyPair.Create();
        return new Member
        {
            MemberId = id,
            DisplayName = name,
            SigningPublicKey = keys.Public.Signing,
            AgreementPublicKey = keys.Public.Agreement,
            JoinedAt = Now,
        };
    }

    private static Family NewFamily() => Family.Create("Sharma Family", NewMember("mom", "Mom"), Now);

    [Fact]
    public void Creator_is_admin_and_topic_is_long_and_random()
    {
        var family = NewFamily();
        Assert.True(family.IsAdmin("mom"));
        Assert.Equal(1, family.KeyEpoch);
        Assert.Equal(34, family.RelayTopic.Length);
        Assert.NotEqual(family.RelayTopic, Family.NewTopic());
        Assert.Matches("^[A-Za-z0-9]+$", family.RelayTopic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string name) =>
        Assert.Throws<FamilyRuleException>(() => Family.Create(name, NewMember("a", "A"), Now));

    [Fact]
    public void Only_admins_add_members_and_new_members_are_not_admins()
    {
        var family = NewFamily();
        Assert.True(family.AddMember(NewMember("dad", "Dad"), approvedBy: "mom"));
        Assert.False(family.IsAdmin("dad"));
        Assert.Throws<FamilyRuleException>(() => family.AddMember(NewMember("kid", "Kid"), approvedBy: "dad"));
    }

    [Fact]
    public void Adding_the_same_member_twice_is_a_no_op()
    {
        var family = NewFamily();
        var dad = NewMember("dad", "Dad");
        family.AddMember(dad, "mom");
        Assert.False(family.AddMember(dad, "mom"));
        Assert.Equal(2, family.Members.Count);
    }

    [Fact]
    public void Removing_a_member_rotates_epoch_and_topic_and_they_cannot_rejoin()
    {
        var family = NewFamily();
        var dad = NewMember("dad", "Dad");
        family.AddMember(dad, "mom");
        var topic = family.RelayTopic;

        family.RemoveMember("dad", removedBy: "mom", Now);
        Assert.Equal(1, family.KeyEpoch); // removal itself doesn't rotate…
        family.RotateKeys("mom");         // …the admin's phone does, right after

        Assert.Equal(2, family.KeyEpoch);
        Assert.NotEqual(topic, family.RelayTopic);
        Assert.False(family.IsActiveMember("dad"));
        Assert.Throws<FamilyRuleException>(() => family.AddMember(dad, "mom"));
    }

    [Fact]
    public void Members_cannot_remove_others_but_can_leave()
    {
        var family = NewFamily();
        family.AddMember(NewMember("dad", "Dad"), "mom");
        family.AddMember(NewMember("kid", "Kid"), "mom");
        Assert.Throws<FamilyRuleException>(() => family.RemoveMember("kid", removedBy: "dad", Now));
        family.RemoveMember("dad", removedBy: "dad", Now);
        Assert.False(family.IsActiveMember("dad"));
    }

    [Fact]
    public void The_last_admin_cannot_leave_or_be_demoted_while_others_remain()
    {
        var family = NewFamily();
        family.AddMember(NewMember("dad", "Dad"), "mom");
        Assert.Throws<FamilyRuleException>(() => family.RemoveMember("mom", "mom", Now));
        Assert.Throws<FamilyRuleException>(() => family.SetRole("mom", MemberRole.Member, "mom"));

        family.SetRole("dad", MemberRole.Admin, "mom");
        family.RemoveMember("mom", "mom", Now);
        Assert.True(family.IsAdmin("dad"));
    }

    [Fact]
    public void Rename_requires_admin()
    {
        var family = NewFamily();
        family.AddMember(NewMember("dad", "Dad"), "mom");
        family.Rename("Sharmas", "mom");
        Assert.Equal("Sharmas", family.Name);
        Assert.Throws<FamilyRuleException>(() => family.Rename("X", "dad"));
    }
}

public class InviteCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static InPersonInvite InPerson(DateTimeOffset expires) =>
        new("inv1", InviteCodec.NewJoinSecret(), "fam1", "Sharma Family", "https://ntfy.sh", Family.NewTopic(), 1, AeadBox.NewKey(), "mom", [1, 2, 3], expires);

    private static RemoteInvite Remote(DateTimeOffset expires, string relay = "https://ntfy.sh") =>
        new("inv1", "fam1", "Sharma Family", relay, Family.NewTopic(), InviteCodec.NewJoinSecret(), "mom", [1], [2], expires);

    [Fact]
    public void In_person_invite_round_trips()
    {
        var invite = InPerson(Now.AddMinutes(15));
        var decoded = Assert.IsType<InPersonInvite>(InviteCodec.Decode(InviteCodec.Encode(invite), Now));
        Assert.Equal(invite.FamilyKey, decoded.FamilyKey);
        Assert.Equal(invite.RelayTopic, decoded.RelayTopic);
    }

    [Fact]
    public void Remote_invite_is_found_inside_a_pasted_whatsapp_message_and_has_no_family_key()
    {
        var invite = Remote(Now.AddHours(24));
        var message = InviteCodec.ShareText(invite);
        var decoded = Assert.IsType<RemoteInvite>(InviteCodec.Decode("Forwarded:\n" + message + "\nThanks", Now));
        Assert.Equal(invite.JoinSecret, decoded.JoinSecret);
        Assert.DoesNotContain("familyKey", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            Pad(InviteCodec.Encode(invite)["tv1:r:".Length..]))), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expired_or_malformed_invites_are_rejected()
    {
        Assert.Null(InviteCodec.Decode(InviteCodec.Encode(InPerson(Now.AddSeconds(-1))), Now));
        Assert.Null(InviteCodec.Decode("tv1:q:not-base64!!", Now));
        Assert.Null(InviteCodec.Decode("hello", Now));
        Assert.Null(InviteCodec.Decode(null, Now));
    }

    [Fact]
    public void Plain_http_relays_are_rejected() =>
        Assert.Null(InviteCodec.Decode(InviteCodec.Encode(Remote(Now.AddHours(1), "http://ntfy.sh")), Now));

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }
}
