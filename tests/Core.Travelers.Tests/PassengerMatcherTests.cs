using TravelVault.Core.Travelers;

namespace TravelVault.Core.Travelers.Tests;

public class PersonNameTests
{
    [Theory]
    [InlineData("MR RAHUL SHARMA", "RAHUL SHARMA")]
    [InlineData("SHARMA/RAHUL MR", "RAHUL SHARMA")]
    [InlineData("SHARMA/RAHULMR", "RAHUL SHARMA")]
    [InlineData("SHARMA/PRIYAMRS", "PRIYA SHARMA")]
    [InlineData("Sharma, Rahul", "RAHUL SHARMA")]
    [InlineData("Ms. Priya   Sharma", "PRIYA SHARMA")]
    [InlineData("José Álvarez", "JOSE ALVAREZ")]
    [InlineData("Seán O'Brien", "SEAN OBRIEN")]
    [InlineData("SMT. LAKSHMI DEVI", "LAKSHMI DEVI")]
    [InlineData("LEE/AMY", "AMY LEE")] // short name: no title stripping
    public void Normalizes_ticket_styles(string raw, string expected) => Assert.Equal(expected, PersonName.Parse(raw).Normalized);

    [Fact]
    public void Empty_and_title_only_names_are_empty()
    {
        Assert.True(PersonName.Parse(null).IsEmpty);
        Assert.True(PersonName.Parse("  ").IsEmpty);
        Assert.True(PersonName.Parse("MR").IsEmpty);
    }

    [Fact]
    public void Title_case_for_display() => Assert.Equal("Rahul K Sharma", PersonName.Parse("RAHUL K SHARMA").ToTitleCase());
}

public class PassengerMatcherTests
{
    private static readonly Traveler Rahul = Make("Rahul Sharma");
    private static readonly Traveler Priya = Make("Priya Sharma");
    private static readonly Traveler Ananya = Make("Ananya Sharma");
    private static readonly Traveler[] Family = [Rahul, Priya, Ananya];

    private static Traveler Make(string name) => Traveler.Create("fam", name);

    [Theory]
    [InlineData("SHARMA/RAHULMR", MatchStrength.Exact)]
    [InlineData("MR RAHUL SHARMA", MatchStrength.Exact)]
    [InlineData("Sharma Rahul", MatchStrength.Exact)]
    [InlineData("RAHUL KUMAR SHARMA", MatchStrength.Good)]
    [InlineData("R SHARMA", MatchStrength.Weak)]
    [InlineData("RAHUL SHARNA", MatchStrength.Weak)] // OCR typo
    public void Finds_rahul(string printed, MatchStrength expected)
    {
        var match = PassengerMatcher.Match(printed, Family);
        Assert.Same(Rahul, match.Traveler);
        Assert.Equal(expected, match.Strength);
    }

    [Fact]
    public void Surname_alone_matches_nobody() => Assert.Null(PassengerMatcher.Match("SHARMA", Family).Traveler);

    [Fact]
    public void Initial_plus_surname_matches_the_only_traveler_with_that_initial()
    {
        var match = PassengerMatcher.Match("A SHARMA", Family);
        Assert.Same(Ananya, match.Traveler);
        Assert.False(match.IsConfident); // weak: the app asks the user to confirm
    }

    [Fact]
    public void Two_equally_good_candidates_means_no_guess()
    {
        var twinA = Make("Arjun Mehta");
        var twinB = Make("Arun Mehta");
        var match = PassengerMatcher.Match("A MEHTA", [twinA, twinB]);
        Assert.Null(match.Traveler);
    }

    [Fact]
    public void Unknown_person_is_not_matched()
    {
        Assert.Null(PassengerMatcher.Match("JOHN DOE", Family).Traveler);
        Assert.Null(PassengerMatcher.Match("", Family).Traveler);
    }

    [Fact]
    public void Learned_alias_gives_exact_match_next_time()
    {
        var grandpa = Make("Thatha");
        Assert.Null(PassengerMatcher.Match("MR VENKATARAMAN S", [grandpa]).Traveler);
        Assert.True(grandpa.AddAlias("MR VENKATARAMAN S"));
        Assert.False(grandpa.AddAlias("Venkataraman S")); // same after normalising
        var match = PassengerMatcher.Match("VENKATARAMAN/S MR", [grandpa]);
        Assert.Equal(MatchStrength.Exact, match.Strength);
    }

    [Fact]
    public void Deleted_travelers_are_ignored()
    {
        var gone = Make("Rahul Sharma");
        gone.IsDeleted = true;
        Assert.Null(PassengerMatcher.Match("RAHUL SHARMA", [gone]).Traveler);
    }

    [Theory]
    [InlineData("KITTEN", "SITTING", 3)]
    [InlineData("SHARMA", "SHARMA", 0)]
    [InlineData("", "ABC", 3)]
    public void Edit_distance(string a, string b, int expected) => Assert.Equal(expected, PassengerMatcher.EditDistance(a, b));
}
