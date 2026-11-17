using System.Globalization;
using TravelVault.Core.Common;
using TravelVault.Core.Relay;

namespace TravelVault.Core.Sync.Tests;

/// <summary>
/// A fake ntfy server shared by all test phones. Like ntfy.sh it keeps messages 12 hours and attachments 3 hours,
/// and "since" returns messages after a given id (or everything kept, if that id has expired).
/// </summary>
internal sealed class InMemoryRelay(IClock clock) : IRelay, IRelayFactory
{
    public static readonly TimeSpan MessageRetention = TimeSpan.FromHours(12);
    public static readonly TimeSpan FileRetention = TimeSpan.FromHours(3);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Stored>> _topics = [];
    private readonly Dictionary<string, (DateTimeOffset At, byte[] Data)> _files = [];
    private long _nextId;

    public int Published { get; private set; }

    public bool Offline { get; set; }

    public IRelay For(string serverUrl) => this;

    public Task<string> PublishAsync(string topic, string text, CancellationToken cancellationToken = default) => Add(topic, text, null);

    public Task<string> PublishFileAsync(string topic, byte[] file, string text, CancellationToken cancellationToken = default)
    {
        if (file.Length > 2 * 1024 * 1024)
        {
            throw new HttpRequestException("ntfy.sh visitor attachment limit is 2 MB");
        }

        var url = new Uri($"https://relay.test/file/{Guid.NewGuid():N}");
        lock (_gate)
        {
            _files[url.ToString()] = (clock.UtcNow, file);
        }

        return Add(topic, text, url);
    }

    public Task<IReadOnlyList<RelayMessage>> FetchAsync(string topic, string? since, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var kept = _topics.GetValueOrDefault(topic, []).Where(m => clock.UtcNow - m.Message.Time < MessageRetention).ToList();
            var index = since is null ? -1 : kept.FindIndex(m => m.Message.Id == since);
            return Task.FromResult<IReadOnlyList<RelayMessage>>(kept.Skip(index + 1).Select(m => m.Message).ToList());
        }
    }

    public Task<byte[]> DownloadAsync(Uri attachmentUrl, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            if (_files.TryGetValue(attachmentUrl.ToString(), out var file) && clock.UtcNow - file.At < FileRetention)
            {
                return Task.FromResult(file.Data);
            }
        }

        throw new HttpRequestException("The file has expired.");
    }

    /// <summary>Everything ever posted to a topic (for attack tests).</summary>
    public IReadOnlyList<RelayMessage> All(string topic)
    {
        lock (_gate)
        {
            return _topics.GetValueOrDefault(topic, []).Select(m => m.Message).ToList();
        }
    }

    private Task<string> Add(string topic, string text, Uri? attachment)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var id = (++_nextId).ToString("D8", CultureInfo.InvariantCulture);
            if (!_topics.TryGetValue(topic, out var list))
            {
                _topics[topic] = list = [];
            }

            list.Add(new Stored(new RelayMessage(id, clock.UtcNow, text, attachment)));
            Published++;
            return Task.FromResult(id);
        }
    }

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new HttpRequestException("No connection");
        }
    }

    private sealed record Stored(RelayMessage Message);
}
