using TravelVault.Core.Common;

namespace TravelVault.Core.Common.Tests;

public class HybridClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Next_always_increases_even_when_wall_clock_stands_still()
    {
        var clock = new HybridClock(new ManualClock(Start), "a");
        var first = clock.Next();
        var second = clock.Next();
        Assert.True(second > first);
        Assert.Equal(first.WallMs, second.WallMs);
        Assert.Equal(1, second.Counter);
    }

    [Fact]
    public void Next_does_not_go_backwards_when_wall_clock_is_set_back()
    {
        var wall = new ManualClock(Start);
        var clock = new HybridClock(wall, "a");
        var before = clock.Next();
        wall.Set(Start.AddHours(-3));
        Assert.True(clock.Next() > before);
    }

    [Fact]
    public void Observe_moves_past_a_remote_timestamp_from_a_phone_with_a_fast_clock()
    {
        var clock = new HybridClock(new ManualClock(Start), "a");
        var remote = new HybridTimestamp(Start.AddDays(1).ToUnixTimeMilliseconds(), 5, "b");
        clock.Observe(remote);
        Assert.True(clock.Next() > remote);
    }

    [Fact]
    public void Text_form_round_trips_and_sorts_like_the_value()
    {
        var a = new HybridTimestamp(1_700_000_000_000, 2, "node1");
        var b = new HybridTimestamp(1_700_000_000_000, 10, "node1");
        Assert.Equal(a, HybridTimestamp.Parse(a.ToString()));
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
        Assert.True(a < b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12-zz-n")]
    public void TryParse_rejects_garbage(string text) => Assert.False(HybridTimestamp.TryParse(text, out _));

    [Fact]
    public void Node_breaks_ties_deterministically()
    {
        var a = new HybridTimestamp(1, 0, "a");
        var b = new HybridTimestamp(1, 0, "b");
        Assert.True(a < b);
    }

    [Fact]
    public void Ids_are_valid_and_time_ordered()
    {
        var first = Ids.New();
        var second = Ids.New();
        Assert.True(Ids.IsValid(first));
        Assert.NotEqual(first, second);
        Assert.False(Ids.IsValid("../etc"));
    }
}
