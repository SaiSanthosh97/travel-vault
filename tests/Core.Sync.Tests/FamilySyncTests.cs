using System.Text;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Sync.Tests;

/// <summary>End-to-end sync between simulated phones (Mom = creator/admin, Dad, Grandpa).</summary>
public sealed class FamilySyncTests : IDisposable
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRelay _relay;
    private readonly List<Phone> _phones = [];

    public FamilySyncTests() => _relay = new InMemoryRelay(_clock);

    private Phone NewPhone(string name, SyncOptions? options = null)
    {
        var phone = new Phone(name, _clock, _relay, options);
        _phones.Add(phone);
        return phone;
    }

    /// <summary>Mom creates the family; Dad joins in person (QR); both fully synced.</summary>
    private async Task<(Phone Mom, Phone Dad, Family Family)> FamilyOfTwoAsync(SyncOptions? options = null)
    {
        var mom = NewPhone("Mom", options);
        var dad = NewPhone("Dad", options);
        var family = await mom.Sync.CreateFamilyAsync("Sharma Family");
        await mom.SyncAsync();

        var qr = InviteCodec.Encode(await mom.Sync.CreateInPersonInviteAsync(family.Id));
        await dad.Sync.JoinInPersonAsync((InPersonInvite)InviteCodec.Decode(qr, _clock.UtcNow)!);
        await Rounds(mom, dad);
        return (mom, dad, family);
    }

    /// <summary>Lets every phone sync a few times, the way background sync would over half an hour.</summary>
    private static async Task Rounds(params Phone[] phones)
    {
        for (var i = 0; i < 3; i++)
        {
            foreach (var phone in phones)
            {
                await phone.SyncAsync();
            }
        }
    }

    [Fact]
    public async Task In_person_join_then_documents_and_files_reach_the_other_phone()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();

        var dadsFamily = (await dad.Families.GetAsync(family.Id))!;
        Assert.True(FamilySync.Admitted(dadsFamily));
        Assert.Equal(2, dadsFamily.ActiveMembers.Count());
        Assert.Contains("Dad joined Sharma Family", mom.Notices);

        var ticket = await mom.ImportAsync(family.Id, "flight-eticket-domestic.txt");
        await Rounds(mom, dad);

        var received = await dad.DocumentService.GetAsync(ticket.Id);
        Assert.NotNull(received);
        Assert.Equal("X7KQ2P", received!.Pnr);
        var file = await dad.DocumentService.OpenFileAsync(ticket.Id);
        Assert.Equal(FileAvailability.Present, file.Availability);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine("samples", "flight-eticket-domestic.txt")), file.Content);
        Assert.Single(await dad.Search.SearchAsync(family.Id, "priya"));
        Assert.Empty(mom.Failures.Concat(dad.Failures));
    }

    [Fact]
    public async Task Edits_flow_back_and_the_newer_version_wins()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        var ticket = await mom.ImportAsync(family.Id, "train-europe.txt");
        await Rounds(mom, dad);

        var onDad = (await dad.DocumentService.GetAsync(ticket.Id))!;
        onDad.Title = "Paris with the kids";
        await dad.DocumentService.UpdateAsync(onDad, [TravelDocument.Fields.Title]);
        await Rounds(dad, mom);

        Assert.Equal("Paris with the kids", (await mom.DocumentService.GetAsync(ticket.Id))!.Title);

        await mom.DocumentService.DeleteAsync(ticket.Id);
        await Rounds(mom, dad);
        Assert.True((await dad.DocumentService.GetAsync(ticket.Id))!.IsDeleted);
    }

    [Fact]
    public async Task Remote_invite_needs_approval_and_both_phones_show_the_same_safety_code()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        await mom.ImportAsync(family.Id, "hotel-booking.txt");
        await Rounds(mom, dad);

        var grandpa = NewPhone("Grandpa");
        var text = InviteCodec.ShareText(await mom.Sync.CreateRemoteInviteAsync(family.Id));
        var invite = (RemoteInvite)InviteCodec.Decode(text, _clock.UtcNow)!;
        var grandpasCode = await grandpa.Sync.RequestRemoteJoinAsync(invite);

        await grandpa.SyncAsync();
        Assert.Equal(JoinOutcome.Waiting, await grandpa.Sync.CheckJoinAsync(invite.InviteId));
        Assert.Null(await grandpa.Families.GetAsync(family.Id)); // nothing without approval

        await mom.SyncAsync();
        var pending = Assert.Single(await mom.Sync.PendingJoinsAsync(family.Id));
        Assert.Equal(grandpasCode, pending.SafetyCode);
        Assert.Equal("Grandpa", pending.Member.DisplayName);

        await mom.Sync.ApproveJoinAsync(family.Id, pending.Member.MemberId);
        await Rounds(grandpa, mom, dad, grandpa);

        Assert.Contains("You joined Sharma Family", grandpa.Notices);
        Assert.Single(await grandpa.DocumentService.ListAsync(family.Id), d => d.Type == DocumentType.Hotel);
        Assert.Equal(3, (await dad.Families.GetAsync(family.Id))!.ActiveMembers.Count());
    }

    [Fact]
    public async Task A_rejected_or_tampered_remote_join_gets_nothing()
    {
        var (mom, _, family) = await FamilyOfTwoAsync();
        var stranger = NewPhone("Stranger");
        var invite = await mom.Sync.CreateRemoteInviteAsync(family.Id);

        // Someone who saw the text tries with a changed secret: the admin's phone ignores the request.
        var forged = invite with { JoinSecret = InviteCodec.NewJoinSecret() };
        await stranger.Sync.RequestRemoteJoinAsync(forged);
        await mom.SyncAsync();
        Assert.Empty(await mom.Sync.PendingJoinsAsync(family.Id));

        await stranger.Sync.RequestRemoteJoinAsync(invite);
        await mom.SyncAsync();
        var pending = Assert.Single(await mom.Sync.PendingJoinsAsync(family.Id));
        await mom.Sync.RejectJoinAsync(family.Id, pending.Member.MemberId);
        Assert.Equal(JoinOutcome.Rejected, await stranger.Sync.CheckJoinAsync(invite.InviteId));
        Assert.Null(await stranger.Families.GetAsync(family.Id));
    }

    [Fact]
    public async Task Removing_a_member_rotates_keys_and_the_removed_phone_gets_nothing_new()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        var grandpa = NewPhone("Grandpa");
        await grandpa.Sync.JoinInPersonAsync(await mom.Sync.CreateInPersonInviteAsync(family.Id));
        await Rounds(grandpa, mom, dad, grandpa);

        await mom.Sync.RemoveMemberAsync(family.Id, dad.Id);
        await Rounds(mom, dad, grandpa);

        Assert.True(await dad.Sync.IsRemovedAsync(family.Id));
        Assert.Contains(dad.Notices, n => n.StartsWith("You were removed", StringComparison.Ordinal));
        var grandpasFamily = (await grandpa.Families.GetAsync(family.Id))!;
        Assert.Equal(2, grandpasFamily.KeyEpoch);
        Assert.NotEqual(family.RelayTopic, grandpasFamily.RelayTopic);

        var secret = await mom.ImportAsync(family.Id, "visa-evisa.txt");
        await Rounds(mom, dad, grandpa);
        Assert.NotNull(await grandpa.DocumentService.GetAsync(secret.Id));
        Assert.Null(await dad.DocumentService.GetAsync(secret.Id));
    }

    [Fact]
    public async Task A_phone_away_longer_than_the_relay_keeps_messages_catches_up_from_family()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        var ticket = await mom.ImportAsync(family.Id, "bus-ticket.txt");
        await mom.SyncAsync();

        _clock.Advance(TimeSpan.FromDays(2)); // Dad's phone was off; the relay has forgotten everything

        await dad.SyncAsync();  // "I was away" → state request
        await mom.SyncAsync();  // answers with everything it holds
        await dad.SyncAsync();  // gets the document, asks for the file
        await mom.SyncAsync();  // sends the file
        await dad.SyncAsync();  // receives the file

        Assert.NotNull(await dad.DocumentService.GetAsync(ticket.Id));
        Assert.Equal(FileAvailability.Present, (await dad.DocumentService.OpenFileAsync(ticket.Id)).Availability);
    }

    [Fact]
    public async Task Private_documents_never_leave_the_phone_and_withdrawing_one_removes_family_copies()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        var shared = await mom.ImportAsync(family.Id, "event-ticket.txt");
        await Rounds(mom, dad);
        Assert.False((await dad.DocumentService.GetAsync(shared.Id))!.IsDeleted);

        await mom.DocumentService.SetVisibilityAsync(shared.Id, Visibility.Private);
        await Rounds(mom, dad);
        var withdrawn = (await dad.DocumentService.GetAsync(shared.Id))!;
        Assert.True(withdrawn.IsDeleted);
        Assert.Null(withdrawn.Pnr); // the tombstone carries no details
        Assert.False((await mom.DocumentService.GetAsync(shared.Id))!.IsDeleted);

        var passport = await mom.ImportAsync(family.Id, "passport-mrz.txt");
        await mom.DocumentService.SetVisibilityAsync(passport.Id, Visibility.Private);
        await Rounds(mom, dad);
        // The import itself was queued before it became private; the outbox sends only the latest signed state,
        // which for a private document is a content-free tombstone.
        var onDad = await dad.DocumentService.GetAsync(passport.Id);
        Assert.True(onDad is null || (onDad.IsDeleted && onDad.Identity is null));
    }

    [Fact]
    public async Task Forged_replayed_and_foreign_messages_are_ignored()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        var ticket = await mom.ImportAsync(family.Id, "flight-us-style.txt");
        await mom.SyncAsync();

        // An attacker who learned the topic posts junk and a message encrypted with the wrong key.
        var attacker = DeviceKeyPair.Create();
        var fake = Envelope.Create(family.Id, "attacker", 1, HybridTimestamp.Zero, Kinds.Changes, new ChangesBody([]));
        await _relay.PublishAsync(family.RelayTopic, WireCodec.Encode(fake, AeadBox.NewKey(), 1, attacker));
        await _relay.PublishAsync(family.RelayTopic, "hello");

        // Replay: the same genuine message posted again later.
        var genuine = _relay.All(family.RelayTopic).First(m => m.Text.StartsWith("tv2.", StringComparison.Ordinal));
        await dad.SyncAsync();
        var title = (await dad.DocumentService.GetAsync(ticket.Id))!.Title;
        await _relay.PublishAsync(family.RelayTopic, genuine.Text);
        await dad.SyncAsync();

        Assert.Equal(title, (await dad.DocumentService.GetAsync(ticket.Id))!.Title);
        Assert.Empty(dad.Failures);
    }

    [Fact]
    public async Task Large_files_travel_in_chunks_and_are_verified()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync(new SyncOptions { ChunkSize = 300 });
        var text = File.ReadAllText(Path.Combine("samples", "hotel-booking.txt")) + "\n" + new string('x', 1500);
        var ticket = await mom.ImportAsync(family.Id, text);
        await Rounds(mom, dad);

        var opened = await dad.DocumentService.OpenFileAsync(ticket.Id);
        Assert.Equal(FileAvailability.Present, opened.Availability);
        Assert.Equal(Encoding.UTF8.GetBytes(text), opened.Content);
    }

    [Fact]
    public async Task Viewing_a_missing_file_asks_family_immediately()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        var ticket = await mom.ImportAsync(family.Id, "flight-connecting-international.txt");
        await mom.SyncAsync();
        _clock.Advance(TimeSpan.FromHours(4)); // file attachments expired; metadata messages still there
        await dad.SyncAsync();
        Assert.Equal(FileAvailability.Requested, (await dad.DocumentService.OpenFileAsync(ticket.Id)).Availability);

        await mom.SyncAsync();
        await dad.SyncAsync();
        Assert.Equal(FileAvailability.Present, (await dad.DocumentService.OpenFileAsync(ticket.Id)).Availability);
    }

    [Fact]
    public async Task Leaving_wipes_the_phone_and_the_admin_rotates_the_key()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        await mom.ImportAsync(family.Id, "event-ticket.txt");
        await Rounds(mom, dad);

        await dad.Sync.LeaveFamilyAsync(family.Id);
        Assert.Null(await dad.Families.GetAsync(family.Id));
        Assert.Empty(await dad.DocumentService.ListAsync(family.Id));

        await mom.SyncAsync();
        var momsFamily = (await mom.Families.GetAsync(family.Id))!;
        Assert.False(momsFamily.IsActiveMember(dad.Id));
        Assert.Equal(2, momsFamily.KeyEpoch);
    }

    [Fact]
    public async Task Offline_changes_wait_in_the_outbox_and_go_out_when_back_online()
    {
        var (mom, dad, family) = await FamilyOfTwoAsync();
        _relay.Offline = true;
        var ticket = await mom.ImportAsync(family.Id, "train-india-style.txt");
        var report = await mom.Sync.SyncFamilyAsync(family.Id);
        Assert.NotEmpty(report.Errors);
        Assert.True(await mom.Sync.PendingCountAsync(family.Id) > 0);

        _relay.Offline = false;
        await Rounds(mom, dad);
        Assert.Equal(0, await mom.Sync.PendingCountAsync(family.Id));
        Assert.NotNull(await dad.DocumentService.GetAsync(ticket.Id));
    }

    public void Dispose()
    {
        foreach (var phone in _phones)
        {
            phone.Dispose();
        }
    }
}

public class WireCodecTests
{
    [Fact]
    public void Envelopes_round_trip_and_are_padded_to_buckets()
    {
        using var keys = DeviceKeyPair.Create();
        var familyKey = AeadBox.NewKey();
        var small = Envelope.Create("fam", "me", 1, HybridTimestamp.Zero, Kinds.CheckIn, new CheckInBody("Mom"));
        var bigger = Envelope.Create("fam", "me", 2, HybridTimestamp.Zero, Kinds.CheckIn, new CheckInBody("Mom with a longer name"));

        var a = WireCodec.Encode(small, familyKey, 3, keys);
        var b = WireCodec.Encode(bigger, familyKey, 3, keys);

        Assert.Equal(a.Length, b.Length); // same bucket: sizes don't reveal content
        Assert.Equal(3, WireCodec.EpochOf(a));
        var decoded = WireCodec.Decode(a, "fam", familyKey)!.Value;
        Assert.Equal("Mom", decoded.Envelope.BodyAs<CheckInBody>()!.Name);
        Assert.True(Signatures.Verify(keys.Public.Signing, decoded.Box, decoded.Signature));
        Assert.Null(WireCodec.Decode(a, "other-family", familyKey));
        Assert.Null(WireCodec.Decode(a, "fam", AeadBox.NewKey()));
    }

    [Fact]
    public void Signed_changes_detect_any_modification()
    {
        using var author = DeviceKeyPair.Create();
        var change = SignedChange.Create("doc", "d1", "fam", new HybridTimestamp(5, 0, "me"), "me", "{\"pnr\":\"X7KQ2P\"}", author);
        Assert.True(change.Verify(author.Public.Signing));
        Assert.False((change with { Payload = "{\"pnr\":\"HACKED\"}" }).Verify(author.Public.Signing));
        Assert.False((change with { Author = "someone" }).Verify(author.Public.Signing));
    }

    [Fact]
    public void Batches_stay_within_the_relay_limit()
    {
        using var keys = DeviceKeyPair.Create();
        var familyKey = AeadBox.NewKey();
        var changes = Enumerable.Range(0, 60)
            .Select(i => SignedChange.Create("doc", Ids.New(), "fam", new HybridTimestamp(i, 0, "me"), "me", Convert.ToBase64String(AeadBox.NewKey()) + i, keys))
            .ToList();
        var batches = WireCodec.Batch(changes, b => WireCodec.Fits(Envelope.Create("fam", "me", 1, HybridTimestamp.Zero, Kinds.Changes, new ChangesBody([.. b])), familyKey, keys));
        Assert.True(batches.Count > 1);
        Assert.Equal(60, batches.Sum(b => b.Count));
        foreach (var batch in batches)
        {
            var text = WireCodec.Encode(Envelope.Create("fam", "me", 1, HybridTimestamp.Zero, Kinds.Changes, new ChangesBody([.. batch])), familyKey, 1, keys);
            Assert.True(text.Length <= WireCodec.MaxTextLength);
        }
    }
}
