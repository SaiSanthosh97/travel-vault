using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Reminders;
using TravelVault.Core.Travelers;

namespace TravelVault.Core.Reminders.Tests;

public class ReminderPlannerTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 4, 30, 0, TimeSpan.Zero); // 10:00 in India

    private static readonly Traveler Rahul = Traveler.Create("fam", "Rahul", linkedMemberId: "rahul-phone");
    private static readonly Traveler Priya = Traveler.Create("fam", "Priya", linkedMemberId: "priya-phone");
    private static readonly Dictionary<string, Traveler> Travelers = new() { [Rahul.Id] = Rahul, [Priya.Id] = Priya };

    private static TravelDocument Flight(DateTime departsLocal, params Traveler[] who) => Flight(departsLocal, "rahul-phone", who);

    private static TravelDocument Flight(DateTime departsLocal, string owner, params Traveler[] who)
    {
        var doc = TravelDocument.Create("fam", DocumentType.Flight, owner, Now);
        doc.Pnr = "X7KQ2P";
        doc.Title = "Flight · Bangalore → New Delhi";
        doc.Segments.Add(new Segment
        {
            Id = Ids.New(),
            Mode = TravelMode.Flight,
            From = new Place("Kempegowda", "BLR", "Bangalore", "IN", "Asia/Kolkata"),
            To = new Place("IGI", "DEL", "New Delhi", "IN", "Asia/Kolkata"),
            Departs = new LocalDateTimeZ(departsLocal, "Asia/Kolkata"),
            ServiceNumber = "6E 2345",
        });
        doc.SetPassengers(who.Select(t => new DocumentPassenger(t.Id, t.DisplayName, Confidence.High)), Rahul.Id);
        return doc;
    }

    [Fact]
    public void Departure_reminders_at_24h_and_3h_in_real_time()
    {
        var doc = Flight(new DateTime(2026, 12, 12, 6, 10, 0), Rahul);
        var plan = ReminderPlanner.Plan([doc], Travelers, "rahul-phone", ReminderSettings.Default, Now, India);

        var departures = plan.Where(r => r.Kind == ReminderKind.Departure).ToList();
        Assert.Equal(2, departures.Count);
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 0, 40, 0, TimeSpan.Zero), departures[0].FireAtUtc); // 06:10 IST = 00:40 UTC, minus 24 h
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 21, 40, 0, TimeSpan.Zero), departures[1].FireAtUtc);
        Assert.Equal("Flight to New Delhi in 3 hours", departures[1].Title);
        Assert.Contains("PNR X7KQ2P", departures[1].Body);
    }

    [Fact]
    public void Missing_boarding_pass_names_the_people_without_one()
    {
        var doc = Flight(new DateTime(2026, 12, 12, 6, 10, 0), Rahul, Priya);
        var plan = ReminderPlanner.Plan([doc], Travelers, "rahul-phone", ReminderSettings.Default, Now, India);
        var missing = plan.Where(r => r.Kind == ReminderKind.MissingBoardingPass).ToList();
        Assert.Equal(2, missing.Count);
        Assert.Equal("Boarding pass missing for Rahul, Priya", missing[0].Title);
    }

    [Fact]
    public void Trip_tomorrow_fires_at_6pm_the_evening_before()
    {
        var doc = Flight(new DateTime(2026, 12, 12, 6, 10, 0), Rahul);
        var tomorrow = ReminderPlanner.Plan([doc], Travelers, "rahul-phone", ReminderSettings.Default, Now, India)
            .Single(r => r.Kind == ReminderKind.TripTomorrow);
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 18, 0, 0, TimeSpan.FromHours(5.5)), tomorrow.FireAtUtc);
        Assert.StartsWith("Tomorrow:", tomorrow.Title);
    }

    [Fact]
    public void Past_events_and_deleted_documents_give_no_reminders()
    {
        var past = Flight(new DateTime(2026, 11, 20, 6, 10, 0), Rahul);
        var deleted = Flight(new DateTime(2026, 12, 20, 6, 10, 0), Rahul);
        deleted.Delete(Now);
        var plan = ReminderPlanner.Plan([past, deleted], Travelers, "rahul-phone", ReminderSettings.Default, Now, India);
        Assert.Empty(plan);
    }

    [Fact]
    public void My_travel_only_skips_other_family_members_trips()
    {
        var priyasTrip = Flight(new DateTime(2026, 12, 12, 6, 10, 0), "priya-phone", Priya);
        var mine = ReminderPlanner.Plan([priyasTrip], Travelers, "rahul-phone", ReminderSettings.Default with { AllFamilyTravel = false }, Now, India);
        Assert.DoesNotContain(mine, r => r.Kind == ReminderKind.Departure);
        var all = ReminderPlanner.Plan([priyasTrip], Travelers, "rahul-phone", ReminderSettings.Default, Now, India);
        Assert.Contains(all, r => r.Kind == ReminderKind.Departure);
    }

    [Fact]
    public void Passport_expiry_reminders_at_90_30_and_7_days()
    {
        var passport = TravelDocument.Create("fam", DocumentType.Passport, "rahul-phone", Now);
        passport.Identity = new IdentityInfo("Z1234567", "IN", null, new DateOnly(2027, 6, 30));
        passport.SetPassengers([new DocumentPassenger(Rahul.Id, null, Confidence.High)], Rahul.Id);
        var expiry = ReminderPlanner.Plan([passport], Travelers, "rahul-phone", ReminderSettings.Default, Now, India)
            .Where(r => r.Kind == ReminderKind.IdentityExpiry).ToList();
        Assert.Equal(3, expiry.Count);
        Assert.Equal("Passport of Rahul expires in 90 days", expiry[0].Title);
    }

    [Fact]
    public void Keys_are_stable_so_replanning_replaces_instead_of_duplicating()
    {
        var doc = Flight(new DateTime(2026, 12, 12, 6, 10, 0), Rahul);
        var first = ReminderPlanner.Plan([doc], Travelers, "rahul-phone", ReminderSettings.Default, Now, India).Select(r => r.Key);
        doc.Segments[0].Departs = new LocalDateTimeZ(new DateTime(2026, 12, 12, 9, 0, 0), "Asia/Kolkata");
        var second = ReminderPlanner.Plan([doc], Travelers, "rahul-phone", ReminderSettings.Default, Now, India).Select(r => r.Key);
        Assert.Equal(first, second);
    }
}

