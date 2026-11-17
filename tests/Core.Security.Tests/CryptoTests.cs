using System.Text;
using TravelVault.Core.Security;

namespace TravelVault.Core.Security.Tests;

public class AeadBoxTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("PNR X7KQ2P");

    [Fact]
    public void Round_trip()
    {
        var key = AeadBox.NewKey();
        Assert.Equal(Data, AeadBox.Open(key, AeadBox.Seal(key, Data, "doc/1"), "doc/1"));
    }

    [Fact]
    public void Same_input_gives_different_output_each_time() // random nonce
    {
        var key = AeadBox.NewKey();
        Assert.NotEqual(AeadBox.Seal(key, Data, "x"), AeadBox.Seal(key, Data, "x"));
    }

    [Fact]
    public void Wrong_label_wrong_key_or_tampering_fails()
    {
        var key = AeadBox.NewKey();
        var box = AeadBox.Seal(key, Data, "file/a");
        Assert.Null(AeadBox.Open(key, box, "file/b"));
        Assert.Null(AeadBox.Open(AeadBox.NewKey(), box, "file/a"));
        box[^1] ^= 1;
        Assert.Null(AeadBox.Open(key, box, "file/a"));
        Assert.Null(AeadBox.Open(key, new byte[3], "file/a"));
    }

    [Fact]
    public void Rejects_short_keys() => Assert.Throws<ArgumentException>(() => AeadBox.Seal(new byte[16], Data, "x"));

    [Fact]
    public void Derived_keys_differ_per_purpose()
    {
        var key = AeadBox.NewKey();
        Assert.NotEqual(KeyDerivation.Derive(key, "db"), KeyDerivation.Derive(key, "msg"));
        Assert.Equal(KeyDerivation.Derive(key, "db"), KeyDerivation.Derive(key, "db"));
    }
}

public class DeviceKeyTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("changes");

    [Fact]
    public void Signature_verifies_only_with_the_signers_key_and_unchanged_data()
    {
        using var alice = DeviceKeyPair.Create();
        using var bob = DeviceKeyPair.Create();
        var signature = alice.Sign(Message);

        Assert.Equal(Signatures.Size, signature.Length);
        Assert.True(Signatures.Verify(alice.Public.Signing, Message, signature));
        Assert.False(Signatures.Verify(bob.Public.Signing, Message, signature));
        Assert.False(Signatures.Verify(alice.Public.Signing, Encoding.UTF8.GetBytes("changed"), signature));
        Assert.False(Signatures.Verify([1, 2, 3], Message, signature));
    }

    [Fact]
    public void Recipient_box_opens_only_for_the_recipient()
    {
        using var joiner = DeviceKeyPair.Create();
        using var other = DeviceKeyPair.Create();
        var familyKey = AeadBox.NewKey();

        var sealedKey = RecipientBox.Seal(joiner.Public.Agreement, familyKey, "family/f1");

        Assert.Equal(familyKey, joiner.OpenSealed(sealedKey, "family/f1"));
        Assert.Null(other.OpenSealed(sealedKey, "family/f1"));
        Assert.Null(joiner.OpenSealed(sealedKey, "family/f2"));
        Assert.Null(joiner.OpenSealed(new byte[10], "family/f1"));
    }

    [Fact]
    public void Private_keys_survive_export_and_import()
    {
        using var original = DeviceKeyPair.Create();
        var (signing, agreement) = original.ExportPrivate();
        using var restored = DeviceKeyPair.Import(signing, agreement);

        Assert.Equal(original.Public.Signing, restored.Public.Signing);
        Assert.True(Signatures.Verify(original.Public.Signing, Message, restored.Sign(Message)));
        var box = RecipientBox.Seal(original.Public.Agreement, Message, "l");
        Assert.Equal(Message, restored.OpenSealed(box, "l"));
    }
}

public class SafetyCodeTests
{
    [Fact]
    public void Safety_code_is_six_digits_and_changes_if_a_key_is_swapped()
    {
        using var admin = DeviceKeyPair.Create();
        using var joiner = DeviceKeyPair.Create();
        using var attacker = DeviceKeyPair.Create();
        var secret = new byte[16];

        var code = SafetyCode.Compute(admin.Public, joiner.Public, secret);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(code, SafetyCode.Compute(admin.Public, joiner.Public, secret));
        Assert.NotEqual(code, SafetyCode.Compute(admin.Public, attacker.Public, secret));
    }
}

public class KeyRingAndFileVaultTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tv-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Master_key_unlocks_with_the_same_vault_only()
    {
        var vault = new InMemoryKeyVault();
        var (ring, wrapped) = KeyRing.CreateNew(vault);
        using var unlocked = KeyRing.Unlock(vault, wrapped);
        Assert.NotNull(unlocked);
        Assert.Equal(ring.DatabaseKey(), unlocked!.DatabaseKey());
        Assert.Null(KeyRing.Unlock(new InMemoryKeyVault(), wrapped));
        ring.Dispose();
    }

    [Fact]
    public void Destroying_the_vault_makes_the_master_key_unrecoverable()
    {
        var vault = new InMemoryKeyVault();
        var (ring, wrapped) = KeyRing.CreateNew(vault);
        ring.Dispose();
        vault.Destroy();
        Assert.Null(KeyRing.Unlock(vault, wrapped));
    }

    [Fact]
    public void Protected_secrets_are_bound_to_their_label()
    {
        var (ring, _) = KeyRing.CreateNew(new InMemoryKeyVault());
        var secret = AeadBox.NewKey();
        var stored = ring.Protect(secret, "family/f1");
        Assert.Equal(secret, ring.Unprotect(stored, "family/f1"));
        Assert.Null(ring.Unprotect(stored, "family/f2"));
    }

    [Fact]
    public async Task File_vault_round_trips_and_detects_tampering()
    {
        var vault = new FileVault(_folder);
        var content = Encoding.UTF8.GetBytes("%PDF-1.7 ticket");
        var key = await vault.WriteAsync("fam1", "file1", content);

        Assert.True(vault.Exists("fam1", "file1"));
        Assert.Equal(content, await vault.ReadAsync("fam1", "file1", key));
        Assert.Null(await vault.ReadAsync("fam1", "file1", AeadBox.NewKey()));

        var path = Path.Combine(_folder, "fam1", "file1.bin");
        var onDisk = await File.ReadAllBytesAsync(path);
        Assert.DoesNotContain("PDF", Encoding.UTF8.GetString(onDisk));
        onDisk[^1] ^= 1;
        await File.WriteAllBytesAsync(path, onDisk);
        Assert.Null(await vault.ReadAsync("fam1", "file1", key));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task File_vault_rejects_path_tricks(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new FileVault(_folder).WriteAsync("fam1", id, [1]));

    [Fact]
    public async Task Deleting_a_family_removes_its_files()
    {
        var vault = new FileVault(_folder);
        await vault.WriteAsync("fam1", "f", [1, 2, 3]);
        Assert.True(vault.SizeOfFamily("fam1") > 0);
        vault.DeleteFamily("fam1");
        Assert.False(vault.Exists("fam1", "f"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
