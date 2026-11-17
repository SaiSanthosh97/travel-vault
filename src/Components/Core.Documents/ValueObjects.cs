namespace TravelVault.Core.Documents;

public enum DocumentType
{
    Other = 0,
    Flight,
    Train,
    Bus,
    Hotel,
    Event,
    Visa,
    Passport,
    BoardingPass,
}

public enum Visibility
{
    /// <summary>Every family member sees it (default).</summary>
    Family = 0,

    /// <summary>Only on the owner's phone (and their backups). Never sent to the relay.</summary>
    Private = 1,
}

public enum TravelMode
{
    Other = 0,
    Flight,
    Train,
    Bus,
    Stay,
    Event,
}

/// <summary>How sure we are about a field. <see cref="UserEdited"/> always wins over parsed values.</summary>
public enum Confidence
{
    Low = 0,
    Medium = 1,
    High = 2,
    UserEdited = 3,
}

/// <summary>
/// A place on a ticket. <see cref="Code"/> is an IATA airport code or station code when known;
/// city/country/time zone come from the bundled airport list when the code is recognised.
/// </summary>
public sealed record Place(string Name, string? Code = null, string? City = null, string? CountryCode = null, string? TimeZoneId = null)
{
    /// <summary>"Delhi (DEL)" or just the name.</summary>
    public string Display => Code is { Length: > 0 } && !string.Equals(Code, City ?? Name, StringComparison.OrdinalIgnoreCase)
        ? $"{City ?? Name} ({Code})"
        : City ?? Name;

    /// <summary>Same city counts as the same place for trip chaining ("Delhi DEL" and "New Delhi station" don't, but two Delhi airports do).</summary>
    public bool SameCityAs(Place? other) =>
        other is not null &&
        (string.Equals(Key(this), Key(other), StringComparison.OrdinalIgnoreCase)
         || (Code is { Length: > 0 } && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase)));

    private static string Key(Place p) => (p.City ?? p.Name).Trim();
}

/// <summary>
/// A time as printed on the ticket (local to the place) plus the place's time zone when known.
/// Tickets always print local times; we show them as printed and only convert to UTC for ordering and reminders.
/// </summary>
public readonly record struct LocalDateTimeZ(DateTime Local, string? TimeZoneId)
{
    /// <summary>The instant. Without a known zone, the phone's own zone is assumed (best effort, flagged by Medium confidence).</summary>
    public DateTimeOffset ToUtc(TimeZoneInfo? fallback = null)
    {
        var zone = Find(TimeZoneId) ?? fallback ?? TimeZoneInfo.Local;
        var unspecified = DateTime.SpecifyKind(Local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1); // inside a DST gap: move to the first valid time
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    public bool HasTime => Local.TimeOfDay != TimeSpan.Zero;

    private static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>Visa / passport details. The number is shown masked in lists.</summary>
public sealed record IdentityInfo(string? Number, string? IssuingCountry, DateOnly? ValidFrom, DateOnly? ExpiresOn, string? VisaCountry = null)
{
    public string MaskedNumber => Number is { Length: > 4 } n ? new string('•', n.Length - 4) + n[^4..] : Number ?? "";
}

/// <summary>Links a boarding pass to the ticket segment and traveler it belongs to.</summary>
public sealed record BoardingPassLink(string TicketDocumentId, string SegmentId, string TravelerId);
