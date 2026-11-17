using TravelVault.Core.Parsing;

namespace TravelVault.Core.Parsing.Tests;

public class BcbpDecoderTests
{
    // The example from the IATA BCBP implementation guide.
    private const string IataExample = "M1DESMARAIS/LUC       EABC123 YULFRAAC 0834 326J001A0025 100";

    [Fact]
    public void Decodes_the_iata_example()
    {
        var pass = BcbpDecoder.Decode(IataExample, new DateOnly(2026, 11, 10))!;
        Assert.Equal("DESMARAIS/LUC", pass.PassengerName);
        Assert.True(pass.ElectronicTicket);
        var leg = Assert.Single(pass.Legs);
        Assert.Equal("ABC123", leg.Pnr);
        Assert.Equal("YUL", leg.From);
        Assert.Equal("FRA", leg.To);
        Assert.Equal("AC", leg.Carrier);
        Assert.Equal("834", leg.FlightNumber);
        Assert.Equal(new DateOnly(2026, 11, 22), leg.Date); // day 326 of 2026
        Assert.Equal('J', leg.Compartment);
        Assert.Equal("1A", leg.Seat);
        Assert.Equal("25", leg.CheckInSequence);
    }

    [Fact]
    public void Decodes_a_two_leg_pass()
    {
        // Leg 1 with no extra data ("00"), then leg 2 (37 mandatory characters).
        const string leg1 = "M2SHARMA/ANANYAMS     EQWE7RT BLRDXBEK 0569 015Y032A0012 100";
        const string leg2 = "QWE7RT DXBLHREK 0007 015Y041K0044 100";
        var pass = BcbpDecoder.Decode(leg1 + leg2, new DateOnly(2027, 1, 10))!;
        Assert.Equal(2, pass.Legs.Count);
        Assert.Equal("DXB", pass.Legs[1].From);
        Assert.Equal("LHR", pass.Legs[1].To);
        Assert.Equal("7", pass.Legs[1].FlightNumber);
        Assert.Equal(new DateOnly(2027, 1, 15), pass.Legs[1].Date);
    }

    [Theory]
    [InlineData(5, "2026-12-20", "2027-01-05")]   // early January seen from late December → next year
    [InlineData(360, "2027-01-03", "2026-12-26")] // late December seen from early January → previous year
    [InlineData(100, "2026-04-01", "2026-04-10")]
    public void Year_is_inferred_as_the_nearest_date(int day, string reference, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), BcbpDecoder.DayOfYearNear(day, DateOnly.Parse(reference)));

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("M1TOO SHORT")]
    [InlineData("M1DESMARAIS/LUC       EABC123 123FRAAC 0834 326J001A0025 100")] // bad airport code
    public void Rejects_non_boarding_pass_data(string text) => Assert.Null(BcbpDecoder.Decode(text, new DateOnly(2026, 1, 1)));
}

public class MrzParserTests
{
    [Fact]
    public void Reads_the_icao_specimen_passport_with_valid_check_digits()
    {
        var text = File.ReadAllText(Path.Combine("samples", "passport-mrz.txt"));
        var mrz = MrzParser.Find(text, new DateOnly(2026, 11, 17))!;
        Assert.Equal(MrzKind.Passport, mrz.Kind);
        Assert.Equal("UTO", mrz.IssuingCountry);
        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.Equal("L898902C3", mrz.DocumentNumber);
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
        Assert.True(mrz.ChecksPassed);
    }

    [Fact]
    public void Repairs_letter_O_read_instead_of_zero_and_spaces_from_ocr()
    {
        const string ocr = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\nL898902C36UTO74O8122F12O4159ZE184226B<<<<< 10";
        var mrz = MrzParser.Find(ocr, new DateOnly(2026, 11, 17))!;
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
    }

    [Theory]
    [InlineData("L898902C3", 6)]
    [InlineData("740812", 2)]
    [InlineData("120415", 9)]
    public void Check_digits_follow_icao_9303(string field, int expected) => Assert.Equal(expected, MrzParser.CheckDigit(field));

    [Fact]
    public void Text_without_an_mrz_gives_nothing() => Assert.Null(MrzParser.Find("PASSPORT\nName: Someone", new DateOnly(2026, 1, 1)));
}
