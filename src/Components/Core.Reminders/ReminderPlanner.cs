using System.Globalization;
using TravelVault.Core.Documents;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;

namespace TravelVault.Core.Reminders;

public enum ReminderKind
{
    Departure,
    MissingBoardingPass,
    TripTomorrow,
    IdentityExpiry,
    Backup,
}

/// <summary>
/// One notification to show at a time. <see cref="Key"/> is stable for the same event, so re-planning after every
/// change replaces reminders instead of duplicating them, and a changed departure time moves its reminder.
/// </summary>
public sealed record PlannedReminder(string Key, ReminderKind Kind, DateTimeOffset FireAtUtc, string Title, string Body, string? DocumentId, string FamilyId);

public sealed record ReminderSettings(bool AllFamilyTravel = true, bool Departure = true, bool MissingBoardingPass = true, bool TripTomorrow = true, bool IdentityExpiry = true)
{
    public static readonly ReminderSettings Default = new();
}

/// <summary>
/// Plans every reminder from the documents on this phone. Runs entirely offline: every phone already has all the
/// family's data, so no push service is needed. The whole plan is recomputed after any change (it's cheap), which
/// is simpler and safer than updating individual alarms.
/// </summary>
public static class ReminderPlanner
{
    private static readonly TimeSpan[] DepartureLeads = [TimeSpan.FromHours(24), TimeSpan.FromHours(3)];
    private static readonly TimeSpan[] BoardingPassLeads = [TimeSpan.FromHours(24), TimeSpan.FromHours(6)];
    private static readonly int[] ExpiryLeadDays = [90, 30, 7];

    public static IReadOnlyList<PlannedReminder> Plan(
        IReadOnlyList<TravelDocument> documents,
        IReadOnlyDictionary<string, Traveler> travelers,
        string myMemberId,
        ReminderSettings settings,
        DateTimeOffset now,
        TimeZoneInfo phoneZone,
        DateTimeOffset? lastBackup = null,
        string familyId = "")
    {
        var list = new List<PlannedReminder>();
        var live = documents.Where(d => !d.IsDeleted).ToList();

        foreach (var doc in live.Where(d => Relevant(d, travelers, myMemberId, settings)))
        {
            if (doc.Type is DocumentType.Visa or DocumentType.Passport)
            {
                PlanExpiry(doc, travelers, settings, now, phoneZone, list);
                continue;
            }

            if (doc.Type == DocumentType.BoardingPass && doc.BoardingPass is not null)
            {
                continue; // the ticket's own reminders cover it
            }

            for (var i = 0; i < doc.Segments.Count; i++)
            {
                var segment = doc.Segments[i];
                if (segment.DepartsUtc is not { } departs || departs <= now)
                {
                    continue;
                }

                if (settings.Departure)
                {
                    foreach (var lead in DepartureLeads)
                    {
                        Add(list, $"dep:{doc.Id}:{segment.Id}:{lead.TotalHours}", ReminderKind.Departure, departs - lead, now,
                            DepartureTitle(segment, lead), Details(doc, segment), doc);
                    }
                }

                if (settings.MissingBoardingPass && segment.Mode == TravelMode.Flight && doc.Type == DocumentType.Flight)
                {
                    var missing = BoardingPassLinker.MissingFor(doc, segment, live);
                    if (missing.Count > 0)
                    {
                        var names = string.Join(", ", missing.Select(t => travelers.TryGetValue(t, out var tr) ? tr.DisplayName : "a traveler"));
                        foreach (var lead in BoardingPassLeads)
                        {
                            Add(list, $"bp:{doc.Id}:{segment.Id}:{lead.TotalHours}", ReminderKind.MissingBoardingPass, departs - lead, now,
                                $"Boarding pass missing for {names}", $"{Route(segment)} · {Local(segment)}", doc);
                        }
                    }
                }

                if (settings.TripTomorrow && i == 0)
                {
                    // 6 pm (phone time) on the day before.
                    var localDeparture = TimeZoneInfo.ConvertTime(departs, phoneZone);
                    var eveningBefore = new DateTimeOffset(localDeparture.Date.AddDays(-1).AddHours(18), phoneZone.GetUtcOffset(localDeparture.Date.AddDays(-1).AddHours(18)));
                    if (departs - eveningBefore >= TimeSpan.FromHours(6))
                    {
                        var who = string.Join(", ", doc.AllTravelerIds().Select(t => travelers.TryGetValue(t, out var tr) ? tr.DisplayName : null).Where(n => n is not null));
                        Add(list, $"tomorrow:{doc.Id}", ReminderKind.TripTomorrow, eveningBefore, now,
                            $"Tomorrow: {doc.Title}", (who.Length > 0 ? who + " · " : "") + Local(segment), doc);
                    }
                }
            }
        }

        if (lastBackup is not null || live.Count > 0)
        {
            var due = (lastBackup ?? now).AddDays(30);
            var at = new DateTimeOffset(due.Date.AddHours(10), phoneZone.GetUtcOffset(due.Date.AddHours(10)));
            Add(list, $"backup:{familyId}", ReminderKind.Backup, at <= now ? now.AddMinutes(1) : at, now.AddSeconds(-1),
                "Back up your travel vault", "Save an encrypted backup so nothing is lost if your phone is.", null, familyId);
        }

        return list.OrderBy(r => r.FireAtUtc).ToList();
    }

    private static void PlanExpiry(TravelDocument doc, IReadOnlyDictionary<string, Traveler> travelers, ReminderSettings settings, DateTimeOffset now, TimeZoneInfo zone, List<PlannedReminder> list)
    {
        if (!settings.IdentityExpiry || doc.Identity?.ExpiresOn is not { } expires)
        {
            return;
        }

        var who = doc.Passengers.Select(p => travelers.TryGetValue(p.TravelerId, out var t) ? t.DisplayName : null).FirstOrDefault(n => n is not null);
        var kind = doc.Type == DocumentType.Visa ? "Visa" : "Passport";
        foreach (var days in ExpiryLeadDays)
        {
            var date = expires.ToDateTime(new TimeOnly(10, 0)).AddDays(-days);
            var at = new DateTimeOffset(date, zone.GetUtcOffset(date));
            Add(list, $"exp:{doc.Id}:{days}", ReminderKind.IdentityExpiry, at, now,
                $"{kind}{(who is null ? "" : " of " + who)} expires in {days} days",
                $"Expires on {expires.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}", doc);
        }
    }

    /// <summary>"My travel only": a document matters if one of its travelers is linked to this member.</summary>
    private static bool Relevant(TravelDocument doc, IReadOnlyDictionary<string, Traveler> travelers, string me, ReminderSettings settings) =>
        settings.AllFamilyTravel
        || doc.OwnerMemberId == me
        || doc.AllTravelerIds().Any(t => travelers.TryGetValue(t, out var traveler) && traveler.LinkedMemberId == me);

    private static void Add(List<PlannedReminder> list, string key, ReminderKind kind, DateTimeOffset at, DateTimeOffset now, string title, string body, TravelDocument? doc, string familyId = "")
    {
        if (at > now)
        {
            list.Add(new PlannedReminder(key, kind, at, title, body, doc?.Id, doc?.FamilyId ?? familyId));
        }
    }

    private static string DepartureTitle(Segment s, TimeSpan lead)
    {
        var when = lead.TotalHours >= 24 ? "tomorrow" : $"in {lead.TotalHours:0} hours";
        return s.Mode switch
        {
            TravelMode.Flight => $"Flight to {Name(s.To)} {when}",
            TravelMode.Train => $"Train to {Name(s.To)} {when}",
            TravelMode.Bus => $"Bus to {Name(s.To)} {when}",
            TravelMode.Stay => $"Hotel check-in {when}: {s.From.Name}",
            TravelMode.Event => $"{s.From.Name} {when}",
            _ => $"Travel {when}",
        };
    }

    private static string Details(TravelDocument d, Segment s)
    {
        var parts = new List<string?> { s.ServiceNumber, Route(s), Local(s), d.Pnr is null ? null : "PNR " + d.Pnr, s.Seat is null ? null : "Seat " + s.Seat };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string Route(Segment s) => s.To is null ? s.From.Display : $"{s.From.Code ?? s.From.Name} → {s.To.Code ?? s.To.Name}";

    private static string Local(Segment s) => DateFormats.Short(s.Departs);

    private static string Name(Place? p) => p?.City ?? p?.Name ?? "your destination";
}
