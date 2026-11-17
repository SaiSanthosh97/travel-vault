using TravelVault.Core.Documents;
using TravelVault.Core.Search;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;

namespace TravelVault.Core.Vault;

/// <summary>Trips: suggestions (minus dismissed ones), confirm/rename/delete, add/remove documents, lists by status.</summary>
public sealed class TripService(VaultSession session, ITripRepository trips, IDocumentRepository documents, ISettingsStore settings, VaultEvents events)
{
    public async Task<IReadOnlyList<TripSuggestion>> SuggestionsAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var dismissed = (await settings.GetAsync(DismissedKey(familyId), cancellationToken) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return TripGrouper.Suggest(await documents.ListAsync(familyId, cancellationToken)).Where(s => !dismissed.Contains(s.Key)).ToList();
    }

    public async Task DismissAsync(string familyId, TripSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(DismissedKey(familyId), cancellationToken) ?? "";
        await settings.SetAsync(DismissedKey(familyId), current + "|" + suggestion.Key, cancellationToken);
    }

    public async Task<Trip> ConfirmAsync(string familyId, TripSuggestion suggestion, string? name = null, CancellationToken cancellationToken = default)
    {
        var trip = Trip.Create(familyId, string.IsNullOrWhiteSpace(name) ? suggestion.Name : name);
        await SaveTripAsync(trip, cancellationToken);
        foreach (var id in suggestion.DocumentIds)
        {
            await SetTripAsync(id, trip.Id, cancellationToken);
        }

        return trip;
    }

    public async Task<Trip> CreateAsync(string familyId, string name, IEnumerable<string> documentIds, CancellationToken cancellationToken = default)
    {
        var trip = Trip.Create(familyId, name);
        await SaveTripAsync(trip, cancellationToken);
        foreach (var id in documentIds)
        {
            await SetTripAsync(id, trip.Id, cancellationToken);
        }

        return trip;
    }

    public async Task RenameAsync(string tripId, string name, CancellationToken cancellationToken = default)
    {
        var trip = await trips.GetAsync(tripId, cancellationToken) ?? throw new InvalidOperationException("Trip not found.");
        trip.Name = name.Trim();
        await SaveTripAsync(trip, cancellationToken);
    }

    public Task AddDocumentAsync(string tripId, string documentId, CancellationToken cancellationToken = default) => SetTripAsync(documentId, tripId, cancellationToken);

    public Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default) => SetTripAsync(documentId, null, cancellationToken);

    /// <summary>Deletes the trip; its documents stay in the vault, just ungrouped.</summary>
    public async Task DeleteAsync(string tripId, CancellationToken cancellationToken = default)
    {
        var trip = await trips.GetAsync(tripId, cancellationToken) ?? throw new InvalidOperationException("Trip not found.");
        foreach (var doc in (await documents.ListAsync(trip.FamilyId, cancellationToken)).Where(d => d.TripId == tripId))
        {
            await SetTripAsync(doc.Id, null, cancellationToken);
        }

        trip.IsDeleted = true;
        await SaveTripAsync(trip, cancellationToken);
    }

    public async Task<IReadOnlyList<TripView>> ListAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var docs = await documents.ListAsync(familyId, cancellationToken);
        return (await trips.ListAsync(familyId, cancellationToken))
            .Select(t => new TripView(t, docs.Where(d => d.TripId == t.Id).OrderBy(d => d.StartsUtc).ToList()))
            .OrderBy(v => v.StartsUtc ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    public async Task<ILookup<TripStatus, TripView>> ByStatusAsync(string familyId, CancellationToken cancellationToken = default) =>
        (await ListAsync(familyId, cancellationToken)).ToLookup(v => v.Status(session.Now));

    private async Task SetTripAsync(string documentId, string? tripId, CancellationToken cancellationToken)
    {
        var doc = await documents.GetAsync(documentId, cancellationToken);
        if (doc is null || doc.TripId == tripId)
        {
            return;
        }

        doc.TripId = tripId;
        session.Stamp(doc);
        await documents.SaveAsync(doc, cancellationToken);
        await events.PublishAsync(new VaultChange(doc.FamilyId, RecordType.Document, doc.Id), cancellationToken);
    }

    private async Task SaveTripAsync(Trip trip, CancellationToken cancellationToken)
    {
        session.Stamp(trip);
        await trips.SaveAsync(trip, cancellationToken);
        await events.PublishAsync(new VaultChange(trip.FamilyId, RecordType.Trip, trip.Id), cancellationToken);
    }

    private static string DismissedKey(string familyId) => $"family.{familyId}.dismissedTrips";
}

/// <summary>
/// Global search. Keeps the full-text index current by listening to vault changes (documents and travelers), and
/// answers queries with the matching live documents, best match first.
/// </summary>
public sealed class SearchService(ISearchIndex index, IDocumentRepository documents, ITravelerRepository travelers) : IVaultChangeHandler
{
    public async Task<IReadOnlyList<TravelDocument>> SearchAsync(string familyId, string text, CancellationToken cancellationToken = default)
    {
        if (SearchQuery.ToFts(text) is not { } query)
        {
            return [];
        }

        var ids = await index.QueryAsync(familyId, query, cancellationToken: cancellationToken);
        var results = new List<TravelDocument>();
        foreach (var id in ids)
        {
            if (await documents.GetAsync(id, cancellationToken) is { IsDeleted: false } doc)
            {
                results.Add(doc);
            }
        }

        return results;
    }

    public async Task HandleAsync(VaultChange change, CancellationToken cancellationToken = default)
    {
        switch (change.Type)
        {
            case RecordType.Document:
                await IndexAsync(change.RecordId, cancellationToken);
                break;
            case RecordType.Traveler:
                // A renamed traveler changes how their documents are found.
                await ReindexFamilyAsync(change.FamilyId, cancellationToken);
                break;
        }
    }

    public async Task ReindexFamilyAsync(string familyId, CancellationToken cancellationToken = default)
    {
        await index.ClearFamilyAsync(familyId, cancellationToken);
        var people = (await travelers.ListAsync(familyId, includeDeleted: true, cancellationToken)).ToDictionary(t => t.Id);
        foreach (var doc in await documents.ListAsync(familyId, cancellationToken))
        {
            await index.UpsertAsync(SearchEntries.For(doc, people), cancellationToken);
        }
    }

    private async Task IndexAsync(string documentId, CancellationToken cancellationToken)
    {
        var doc = await documents.GetAsync(documentId, cancellationToken);
        if (doc is null || doc.IsDeleted)
        {
            await index.RemoveAsync(documentId, cancellationToken);
            return;
        }

        var people = (await travelers.ListAsync(doc.FamilyId, includeDeleted: true, cancellationToken)).ToDictionary(t => t.Id);
        await index.UpsertAsync(SearchEntries.For(doc, people), cancellationToken);
    }
}
