namespace TravelVault.Core.Common;

/// <summary>Source of the current time. Tests use a fixed clock so time-based rules are repeatable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>A clock that only moves when told to. For tests.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;

    public void Set(DateTimeOffset to) => UtcNow = to;
}

/// <summary>New record ids: time-ordered GUIDs (version 7), so ids created later sort later in indexes.</summary>
public static class Ids
{
    public static string New() => Guid.CreateVersion7().ToString("N");

    public static bool IsValid(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
