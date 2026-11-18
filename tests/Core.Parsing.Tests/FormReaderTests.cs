using TravelVault.Core.Parsing;

namespace TravelVault.Core.Parsing.Tests;

public class FormReaderTests
{
    private static Dictionary<string, string> Read(string text) =>
        FormReader.Read(TextLayout.FromGrid(text)).Fields.GroupBy(f => f.Label).ToDictionary(g => g.Key, g => g.First().Value);

    [Fact]
    public void Value_under_a_column_header()
    {
        var fields = Read("PNR:          Train No./Name:        Class:\n1234567890    12627/KARNATAKA EXP    AC 3 TIER (3A)");
        Assert.Equal("1234567890", fields["PNR"]);
        Assert.Equal("12627/KARNATAKA EXP", fields["Train No./Name"]);
        Assert.Equal("AC 3 TIER (3A)", fields["Class"]);
    }

    [Fact]
    public void Several_pairs_on_one_line_and_a_value_after_a_gap()
    {
        var fields = Read("PNR No: 2647193058  Quota: TATKAL (TQ)\nStatus:      Confirmed");
        Assert.Equal("2647193058", fields["PNR No"]);
        Assert.Equal("TATKAL (TQ)", fields["Quota"]);
        Assert.Equal("Confirmed", fields["Status"]);
    }

    [Fact]
    public void Colon_less_run_of_labels()
    {
        var fields = Read("Name Mr A Kumar From Pune To Goa Flight No. 6E 101 Boarding Time 06:15");
        Assert.Equal("Mr A Kumar", fields["Name"]);
        Assert.Equal("Pune", fields["From"]);
        Assert.Equal("Goa", fields["To"]);
        Assert.Equal("6E 101", fields["Flight No"]);
        Assert.Equal("06:15", fields["Boarding Time"]);
    }

    [Fact]
    public void Labels_stacked_above_their_values()
    {
        var fields = Read("Booking Reference\nStatus\nABC123\nConfirmed");
        Assert.Equal("ABC123", fields["Booking Reference"]);
        Assert.Equal("Confirmed", fields["Status"]);
    }

    [Fact]
    public void Sentences_are_not_labels()
    {
        var fields = Read("To view or cancel your ticket please visit the website\nfrom 14:00");
        Assert.Empty(fields);
    }

    [Fact]
    public void Ocr_style_boxes_on_slightly_different_heights_form_one_row()
    {
        var layout = TextLayout.FromWords(
        [
            new LayoutWord("PNR", 40, 100, 70, 112),
            new LayoutWord("Train", 300, 101, 340, 113),
            new LayoutWord("4521378965", 40, 120, 130, 134),
            new LayoutWord("12627", 300, 121, 345, 135),
            new LayoutWord("KARNATAKA", 350, 121, 440, 135),
            new LayoutWord("EXP", 446, 121, 476, 135),
        ]);
        var fields = FormReader.Read(layout).Fields.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("4521378965", fields["PNR"]);
        Assert.Equal("12627 KARNATAKA EXP", fields["Train"]);
    }
}
