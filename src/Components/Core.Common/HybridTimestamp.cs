using System.Globalization;

namespace TravelVault.Core.Common;

/// <summary>
/// A Hybrid Logical Clock value: wall-clock milliseconds + a counter + the id of the device that made it.
/// Used as the version of every synced record. Ordering is total: (wall, counter, node).
/// Unlike plain wall time, an HLC never goes backwards on a device and always moves past any version
/// the device has already seen, so a phone with a wrong clock can't make its edits "older" than ones it saw.
/// </summary>
public readonly record struct HybridTimestamp(long WallMs, int Counter, string Node) : IComparable<HybridTimestamp>
{
    public static readonly HybridTimestamp Zero = new(0, 0, "");

    public int CompareTo(HybridTimestamp other)
    {
        var c = WallMs.CompareTo(other.WallMs);
        if (c != 0)
        {
            return c;
        }

        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Node, other.Node);
    }

    public static bool operator <(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) < 0;

    public static bool operator >(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) > 0;

    public static bool operator <=(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) <= 0;

    public static bool operator >=(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) >= 0;

    /// <summary>Fixed-width text form that sorts the same way as the value (useful in SQL): wall-counter-node.</summary>
    public override string ToString() =>
        WallMs.ToString("D15", CultureInfo.InvariantCulture) + "-" + Counter.ToString("X4", CultureInfo.InvariantCulture) + "-" + Node;

    public static HybridTimestamp Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException($"Not a hybrid timestamp: '{text}'.");

    public static bool TryParse(string? text, out HybridTimestamp value)
    {
        value = Zero;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parts = text.Split('-', 3);
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var wall)
            || !int.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var counter))
        {
            return false;
        }

        value = new HybridTimestamp(wall, counter, parts[2]);
        return true;
    }
}

/// <summary>Issues hybrid timestamps for one device. Thread-safe.</summary>
public sealed class HybridClock(IClock clock, string node)
{
    private readonly Lock _gate = new();
    private HybridTimestamp _last = HybridTimestamp.Zero;

    public string Node { get; } = node;

    /// <summary>A new timestamp, greater than every timestamp issued or observed before.</summary>
    public HybridTimestamp Next()
    {
        lock (_gate)
        {
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            _last = now > _last.WallMs
                ? new HybridTimestamp(now, 0, Node)
                : new HybridTimestamp(_last.WallMs, _last.Counter + 1, Node);
            return _last;
        }
    }

    /// <summary>Moves the clock past a timestamp received from another device.</summary>
    public void Observe(HybridTimestamp remote)
    {
        lock (_gate)
        {
            if (remote.WallMs > _last.WallMs || (remote.WallMs == _last.WallMs && remote.Counter > _last.Counter))
            {
                _last = new HybridTimestamp(remote.WallMs, remote.Counter, Node);
            }
        }
    }
}
