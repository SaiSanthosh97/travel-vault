using TravelVault.Core.Common;
using TravelVault.Core.Security;
using TravelVault.Storage.Sqlite;

namespace TravelVault.Storage.Sqlite.Tests;

/// <summary>First launch, unlock and erase (moved here from the removed backup tests, C-020).</summary>
public sealed class BootstrapperTests : IDisposable
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly List<string> _folders = [];

    private string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tv-boot-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        return folder;
    }

    [Fact]
    public async Task Create_then_unlock_gives_the_same_identity_and_erase_makes_it_unreadable()
    {
        var keyVault = new InMemoryKeyVault();
        var boot = new VaultBootstrapper(NewFolder(), keyVault, _clock);
        Assert.False(boot.IsSetUp);

        var created = await boot.CreateAsync("Rahul");
        var memberId = created.Session.MemberId;
        var signing = created.Session.DeviceKeys.Public.Signing;
        created.Dispose();

        Assert.True(boot.IsSetUp);
        using (var unlocked = (await boot.UnlockAsync())!)
        {
            Assert.Equal(memberId, unlocked.Session.MemberId);
            Assert.Equal("Rahul", unlocked.Session.DisplayName);
            Assert.Equal(signing, unlocked.Session.DeviceKeys.Public.Signing);
        }

        boot.EraseEverything();
        Assert.Null(await boot.UnlockAsync());
    }

    [Fact]
    public async Task A_different_key_vault_cannot_unlock_the_files()
    {
        var folder = NewFolder();
        (await new VaultBootstrapper(folder, new InMemoryKeyVault(), _clock).CreateAsync("Rahul")).Dispose();
        Assert.Null(await new VaultBootstrapper(folder, new InMemoryKeyVault(), _clock).UnlockAsync());
    }

    [Fact]
    public async Task A_name_is_required() =>
        await Assert.ThrowsAsync<ArgumentException>(() => new VaultBootstrapper(NewFolder(), new InMemoryKeyVault(), _clock).CreateAsync("  "));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var folder in _folders.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
