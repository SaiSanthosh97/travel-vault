using TravelVault.Core.Parsing;

namespace TravelVault.Core.Parsing.Tests;

public class TextFactsTests
{
    private static readonly DateOnly Reference = new(2026, 11, 17);

    [Theory]
    [InlineData("Sat, 12 Dec 2026 06:10", 2026, 12, 12, 6, 10)]
    [InlineData("12-Dec-26 at 6:10 PM", 2026, 12, 12, 18, 10)]
    [InlineData("15Jan2027 04:25", 2027, 1, 15, 4, 25)]
    [InlineData("Mar 5, 2027 at 8:15 AM", 2027, 3, 5, 8, 15)]
    [InlineData("2026-12-20 19:20", 2026, 12, 20, 19, 20)]
    [InlineData("Departure: 19:20 20-Dec-2026", 2026, 12, 20, 19, 20)]
    [InlineData("14 February 2027, 09:31", 2027, 2, 14, 9, 31)]
    [InlineData("Reporting 18/01/2027 2130 hrs", 2027, 1, 18, 21, 30)]
    [InlineData("Sat 4 Sep 2027, 7:00 PM", 2027, 9, 4, 19, 0)]
    public void Finds_dates_with_their_times(string text, int y, int mo, int d, int h, int mi)
    {
        var mention = Assert.Single(TextFacts.Analyze(text, Reference).DateTimes);
        Assert.Equal(new DateOnly(y, mo, d), mention.Date);
        Assert.Equal(new TimeOnly(h, mi), mention.Time);
    }

    [Fact]
    public void Missing_year_is_the_next_occurrence()
    {
        Assert.Equal(new DateOnly(2026, 12, 12), TextFacts.Analyze("12 Dec", Reference).DateTimes[0].Date);
        Assert.Equal(new DateOnly(2027, 3, 1), TextFacts.Analyze("1 Mar", Reference).DateTimes[0].Date);
    }

    [Fact]
    public void Numeric_dates_prefer_day_first_and_flag_ambiguity()
    {
        var ambiguous = TextFacts.Analyze("05/03/2027", Reference).DateTimes[0];
        Assert.Equal(new DateOnly(2027, 3, 5), ambiguous.Date);
        Assert.True(ambiguous.Ambiguous);

        var usStyle = TextFacts.Analyze("03/25/2027", Reference).DateTimes[0];
        Assert.Equal(new DateOnly(2027, 3, 25), usStyle.Date);
        Assert.False(usStyle.Ambiguous);
    }

    [Fact]
    public void A_time_on_the_line_after_a_date_belongs_to_it()
    {
        var mention = Assert.Single(TextFacts.Analyze("Date: 12 Dec 2026\n06:10", Reference).DateTimes);
        Assert.Equal(new TimeOnly(6, 10), mention.Time);
    }

    [Fact]
    public void Label_values_and_codes()
    {
        var facts = TextFacts.Analyze("Booking Status: CONFIRMED\nPNR: X7KQ2P\nBooking ID:\nNF2611781234", Reference);
        Assert.Equal("X7KQ2P", facts.LabelCode("pnr"));
        Assert.Equal("NF2611781234", facts.LabelCode(@"booking\s*id"));
        Assert.Null(TextFacts.FirstCode("CONFIRMED"));
    }

    [Theory]
    [InlineData("1. Mr Rahul Sharma (Adult)    Seat 14C", "Mr Rahul Sharma")]
    [InlineData("RAHUL SHARMA   34  M   CNF/B4/32", "RAHUL SHARMA")]
    [InlineData("Mr Vikram Rao, Male, 41", "Mr Vikram Rao")]
    [InlineData("SHARMA/ANANYA MS", "SHARMA/ANANYA MS")]
    [InlineData("Seat 14C", null)]
    [InlineData("Departure Terminal", null)]
    public void Cleans_names(string raw, string? expected) => Assert.Equal(expected, TextFacts.CleanName(raw));

    [Fact]
    public void Finds_names_under_a_header_and_after_labels()
    {
        var facts = TextFacts.Analyze("Passengers\nEmma Thompson\nOliver Thompson\nSynthetic sample.\nGuest name: Rahul Sharma", Reference);
        Assert.Equal(["Emma Thompson", "Oliver Thompson", "Rahul Sharma"], facts.PersonNames());
    }
}
