using System.Text;
using TravelVault.Core.Documents;
using TravelVault.Core.Parsing;
using TravelVault.Core.Travelers;

namespace TravelVault.Core.Search;

/// <summary>One row of the full-text index. Columns are weighted differently when ranking (refs and places count most).</summary>
public sealed record SearchEntry(
    string DocumentId,
    string FamilyId,
    string Title,
    string Provider,
    string Refs,
    string Places,
    string Passengers,
    string Notes);

public interface ISearchIndex
{
    Task UpsertAsync(SearchEntry entry, CancellationToken cancellationToken = default);

    Task RemoveAsync(string documentId, CancellationToken cancellationToken = default);

    /// <returns>Matching document ids, best match first.</returns>
    Task<IReadOnlyList<string>> QueryAsync(string familyId, string ftsQuery, int limit = 200, CancellationToken cancellationToken = default);

    Task ClearFamilyAsync(string familyId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the searchable text for a document: codes AND names of every place, plus other names of the city
/// (so "Bengaluru", "Bangalore" and "BLR" all find the same flight), passenger names as printed AND their profile
/// names, flight/train numbers with and without spaces ("6E 2345" and "6E2345").
/// </summary>
public static class SearchEntries
{
    public static SearchEntry For(TravelDocument document, IReadOnlyDictionary<string, Traveler> travelers)
    {
        var places = new List<string>();
        foreach (var segment in document.Segments)
        {
            foreach (var place in new[] { segment.From, segment.To })
            {
                if (place is null)
                {
                    continue;
                }

                places.Add(place.Name);
                places.Add(place.Code ?? "");
                places.Add(place.CountryCode ?? "");
                foreach (var city in AirportDirectory.Instance.CityNames(place.City))
                {
                    places.Add(city);
                }
            }
        }

        var refs = new List<string?> { document.Pnr, document.BookingReference, document.Identity?.Number };
        foreach (var segment in document.Segments)
        {
            refs.Add(segment.ServiceNumber);
            refs.Add(segment.ServiceNumber?.Replace(" ", "", StringComparison.Ordinal));
        }

        var people = new List<string?>();
        foreach (var passenger in document.Passengers)
        {
            people.Add(passenger.RawName);
            if (travelers.TryGetValue(passenger.TravelerId, out var traveler))
            {
                people.Add(traveler.DisplayName);
                people.AddRange(traveler.Aliases);
            }
        }

        return new SearchEntry(
            document.Id,
            document.FamilyId,
            document.Title + " " + document.Type,
            document.Provider ?? "",
            Join(refs),
            Join(places),
            Join(people),
            document.Notes ?? "");
    }

    private static string Join(IEnumerable<string?> values) =>
        string.Join(' ', values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Turns what the user typed into an FTS5 query that can't break or inject syntax: each word is quoted and
/// prefix-matched, words are AND-ed. "del 6e" → <c>"del"* "6e"*</c>. Punctuation splits words ("X7KQ-2P" → "X7KQ" "2P").
/// </summary>
public static class SearchQuery
{
    public static string? ToFts(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return null;
        }

        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in userText.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words.Count == 0 ? null : string.Join(' ', words.Take(8).Select(w => "\"" + w + "\"*"));
    }
}
