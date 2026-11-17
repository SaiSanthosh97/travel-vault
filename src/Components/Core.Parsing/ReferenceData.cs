using System.Reflection;
using TravelVault.Core.Documents;

namespace TravelVault.Core.Parsing;

public sealed record Airport(string Code, string Name, string City, string CountryCode, string TimeZoneId)
{
    public Place ToPlace() => new(Name, Code, City, CountryCode, TimeZoneId);
}

/// <summary>
/// The bundled list of ~7,900 IATA airports (code → name, city, country, IANA time zone), from the MIT-licensed
/// mwgg/Airports dataset. Lets the app turn "BLR" into "Bangalore, IN, Asia/Kolkata" fully offline, which gives
/// correct reminder times across zones and city-name search.
/// </summary>
public sealed class AirportDirectory
{
    private static readonly Lazy<AirportDirectory> Shared = new(Load);
    private readonly Dictionary<string, Airport> _byCode;
    private readonly Dictionary<string, List<Airport>> _byCity;
    private readonly Dictionary<string, string> _cityAliases;

    private AirportDirectory(IEnumerable<Airport> airports, Dictionary<string, string> cityAliases)
    {
        _byCode = airports.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        _byCity = _byCode.Values
            .GroupBy(a => a.City, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        _cityAliases = cityAliases;
    }

    public static AirportDirectory Instance => Shared.Value;

    public int Count => _byCode.Count;

    public Airport? Find(string? code) => code is { Length: 3 } && _byCode.TryGetValue(code, out var a) ? a : null;

    /// <summary>Airports of a city, accepting other names ("Bengaluru" → Bangalore).</summary>
    public IReadOnlyList<Airport> FindByCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return [];
        }

        var name = city.Trim();
        if (_byCity.TryGetValue(name, out var list))
        {
            return list;
        }

        return _cityAliases.TryGetValue(name, out var canonical) && _byCity.TryGetValue(canonical, out var aliased) ? aliased : [];
    }

    /// <summary>All the names a city is known by (for search): "Bangalore" → ["Bangalore", "Bengaluru"].</summary>
    public IEnumerable<string> CityNames(string? city)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            yield break;
        }

        yield return city;
        var canonical = _cityAliases.TryGetValue(city, out var c) ? c : city;
        if (!canonical.Equals(city, StringComparison.OrdinalIgnoreCase))
        {
            yield return canonical;
        }

        foreach (var (alias, target) in _cityAliases)
        {
            if (target.Equals(canonical, StringComparison.OrdinalIgnoreCase) && !alias.Equals(city, StringComparison.OrdinalIgnoreCase))
            {
                yield return alias;
            }
        }
    }

    private static AirportDirectory Load()
    {
        var airports = ReadTsv("airports.tsv")
            .Where(c => c.Length >= 5)
            .Select(c => new Airport(c[0], c[1], c[2].Length > 0 ? c[2] : c[1], c[3], c[4]));
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadTsv("city-aliases.tsv"))
        {
            foreach (var alias in row.Skip(1))
            {
                aliases[alias] = row[0];
            }
        }

        return new AirportDirectory(airports, aliases);
    }

    internal static IEnumerable<string[]> ReadTsv(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                yield return line.Split('\t');
            }
        }
    }
}

/// <summary>Airline designators ("6E" → IndiGo) for validating flight numbers and naming the provider.</summary>
public static class Airlines
{
    private static readonly Lazy<Dictionary<string, string>> ByCode = new(() =>
        AirportDirectory.ReadTsv("airlines.tsv").Where(r => r.Length >= 2).ToDictionary(r => r[0], r => r[1], StringComparer.OrdinalIgnoreCase));

    public static string? Name(string? code) => code is { Length: 2 } && ByCode.Value.TryGetValue(code, out var n) ? n : null;

    public static bool IsKnown(string? code) => Name(code) is not null;

    /// <summary>Finds an airline mentioned by name in the text ("IndiGo", "Emirates").</summary>
    public static (string Code, string Name)? FindByName(string text)
    {
        foreach (var (code, name) in ByCode.Value)
        {
            if (name.Length >= 4 && text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return (code, name);
            }
        }

        return null;
    }
}

/// <summary>Well-known booking brands, used to name the provider when no airline code applies.</summary>
public static class KnownProviders
{
    private static readonly (string Name, DocumentType Hint)[] Brands =
    [
        ("Booking.com", DocumentType.Hotel), ("Airbnb", DocumentType.Hotel), ("Expedia", DocumentType.Other), ("Agoda", DocumentType.Hotel),
        ("Hotels.com", DocumentType.Hotel), ("Marriott", DocumentType.Hotel), ("Hilton", DocumentType.Hotel), ("Hyatt", DocumentType.Hotel),
        ("IHG", DocumentType.Hotel), ("Accor", DocumentType.Hotel), ("Taj", DocumentType.Hotel), ("OYO", DocumentType.Hotel), ("Treebo", DocumentType.Hotel),
        ("MakeMyTrip", DocumentType.Other), ("Goibibo", DocumentType.Other), ("Cleartrip", DocumentType.Other), ("Yatra", DocumentType.Other),
        ("EaseMyTrip", DocumentType.Other), ("ixigo", DocumentType.Other), ("Skyscanner", DocumentType.Other), ("Trip.com", DocumentType.Other), ("Kiwi.com", DocumentType.Other),
        ("IRCTC", DocumentType.Train), ("Amtrak", DocumentType.Train), ("Eurostar", DocumentType.Train), ("Trainline", DocumentType.Train),
        ("Deutsche Bahn", DocumentType.Train), ("SNCF", DocumentType.Train), ("Renfe", DocumentType.Train), ("Trenitalia", DocumentType.Train), ("National Rail", DocumentType.Train),
        ("FlixBus", DocumentType.Bus), ("redBus", DocumentType.Bus), ("AbhiBus", DocumentType.Bus), ("Greyhound", DocumentType.Bus), ("Megabus", DocumentType.Bus),
        ("National Express", DocumentType.Bus), ("KSRTC", DocumentType.Bus), ("MSRTC", DocumentType.Bus),
        ("Ticketmaster", DocumentType.Event), ("BookMyShow", DocumentType.Event), ("Eventbrite", DocumentType.Event), ("Paytm Insider", DocumentType.Event),
        ("District", DocumentType.Event), ("StubHub", DocumentType.Event), ("SeatGeek", DocumentType.Event), ("Live Nation", DocumentType.Event),
    ];

    public static (string Name, DocumentType Hint)? Find(string text)
    {
        foreach (var brand in Brands)
        {
            // Whole-word match, so "Taj" doesn't match "Tajikistan".
            var index = text.IndexOf(brand.Name, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                var end = index + brand.Name.Length;
                var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (before && after)
                {
                    return brand;
                }

                index = text.IndexOf(brand.Name, end, StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }
}
