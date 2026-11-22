using System.Globalization;
using TravelVault.App.Resources.Strings;
using TravelVault.Core.Documents;
using TravelVault.Core.Travelers;

using Visibility = TravelVault.Core.Documents.Visibility;

namespace TravelVault.App.Services;

/// <summary>A document as one row in a list.</summary>
public sealed record DocumentRow(string Id, string Icon, string Title, string Subtitle, string When, string? Badge, Color? BadgeColor, bool IsPrivate);

/// <summary>Turns domain objects into the text people see. Status colours are always paired with words (accessibility).</summary>
public static class Display
{
    public static string Icon(DocumentType type) => type switch
    {
        DocumentType.Flight => "✈",
        DocumentType.BoardingPass => "🎫",
        DocumentType.Train => "🚆",
        DocumentType.Bus => "🚌",
        DocumentType.Hotel => "🏨",
        DocumentType.Event => "🎟",
        DocumentType.Visa => "🛂",
        DocumentType.Passport => "📘",
        _ => "📄",
    };

    public static string TypeName(DocumentType type) => type switch
    {
        DocumentType.Flight => AppResources.Type_Flight,
        DocumentType.BoardingPass => AppResources.Type_BoardingPass,
        DocumentType.Train => AppResources.Type_Train,
        DocumentType.Bus => AppResources.Type_Bus,
        DocumentType.Hotel => AppResources.Type_Hotel,
        DocumentType.Event => AppResources.Type_Event,
        DocumentType.Visa => AppResources.Type_Visa,
        DocumentType.Passport => AppResources.Type_Passport,
        _ => AppResources.Type_Other,
    };

    public static string When(LocalDateTimeZ? time) =>
        time is { } t ? t.Local.ToString(t.HasTime ? "ddd d MMM yyyy, HH:mm" : "ddd d MMM yyyy", CultureInfo.CurrentCulture) : "";

    public static string Route(Segment s) => s.To is null ? s.From.Display : $"{s.From.Display} → {s.To.Display}";

    public static string Travelers(TravelDocument d, IReadOnlyDictionary<string, Traveler> travelers) =>
        string.Join(", ", d.AllTravelerIds().Select(id => travelers.TryGetValue(id, out var t) ? t.DisplayName : null).Where(n => n is not null));

    public static DocumentRow Row(TravelDocument d, IReadOnlyDictionary<string, Traveler> travelers, DateTimeOffset now)
    {
        var first = d.Segments.FirstOrDefault();
        var subtitle = string.Join(" · ", new[]
        {
            first?.ServiceNumber,
            d.Pnr is null ? null : "PNR " + d.Pnr,
            Travelers(d, travelers),
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string? badge = null;
        Color? color = null;
        if (d.Identity?.ExpiresOn is { } expires)
        {
            var days = expires.DayNumber - DateOnly.FromDateTime(now.LocalDateTime).DayNumber;
            (badge, color) = days < 0 ? (AppResources.Badge_Expired, Danger) : days <= 90 ? (string.Format(AppResources.Badge_ExpiresIn, days), Warning) : (null, null);
        }

        var when = d.Identity?.ExpiresOn is { } e
            ? string.Format(AppResources.Document_ValidUntil, e.ToString("d MMM yyyy", CultureInfo.CurrentCulture))
            : When(first?.Departs);
        return new DocumentRow(d.Id, Icon(d.Type), d.Title, subtitle, when, badge, color, d.Visibility == Visibility.Private);
    }

    public static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? AppResources.Time_JustNow
        : span.TotalHours < 1 ? string.Format(AppResources.Time_MinutesAgo, (int)span.TotalMinutes)
        : span.TotalDays < 1 ? string.Format(AppResources.Time_HoursAgo, (int)span.TotalHours)
        : string.Format(AppResources.Time_DaysAgo, (int)span.TotalDays);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    public static Color Danger => (Color)Application.Current!.Resources["Danger"];

    public static Color Warning => (Color)Application.Current!.Resources["Warning"];

    public static Color Ok => (Color)Application.Current!.Resources["Ok"];
}
