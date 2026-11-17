using TravelVault.Core.Travelers;

namespace TravelVault.Core.Vault;

/// <summary>Traveler profiles: create, rename, aliases, link to members, and "my own traveler" per family.</summary>
public sealed class TravelerService(VaultSession session, ITravelerRepository travelers, VaultEvents events)
{
    public Task<IReadOnlyList<Traveler>> ListAsync(string familyId, CancellationToken cancellationToken = default) =>
        travelers.ListAsync(familyId, cancellationToken: cancellationToken);

    public async Task<IReadOnlyDictionary<string, Traveler>> MapAsync(string familyId, CancellationToken cancellationToken = default) =>
        (await travelers.ListAsync(familyId, includeDeleted: true, cancellationToken)).ToDictionary(t => t.Id);

    public async Task<Traveler> CreateAsync(string familyId, string displayName, string? linkedMemberId = null, CancellationToken cancellationToken = default)
    {
        var traveler = Traveler.Create(familyId, displayName, linkedMemberId);
        await SaveAsync(traveler, cancellationToken);
        return traveler;
    }

    /// <summary>The traveler profile linked to this phone's member (created on first use). Default passenger of no-name tickets.</summary>
    /// <remarks>
    /// If a profile with exactly my name already exists (e.g. created from a ticket I imported), it is adopted and
    /// linked to me rather than creating a second "Rahul Sharma": two identical profiles would make every future
    /// match ambiguous.
    /// </remarks>
    public async Task<Traveler> MyTravelerAsync(string familyId, CancellationToken cancellationToken = default)
    {
        var all = await travelers.ListAsync(familyId, cancellationToken: cancellationToken);
        if (all.FirstOrDefault(t => t.LinkedMemberId == session.MemberId) is { } linked)
        {
            return linked;
        }

        var match = PassengerMatcher.Match(session.DisplayName, all.Where(t => t.LinkedMemberId is null).ToList());
        if (match is { Strength: MatchStrength.Exact, Traveler: { } existing })
        {
            existing.LinkedMemberId = session.MemberId;
            await SaveAsync(existing, cancellationToken);
            return existing;
        }

        return await CreateAsync(familyId, session.DisplayName, session.MemberId, cancellationToken);
    }

    public async Task RenameAsync(string travelerId, string newName, CancellationToken cancellationToken = default)
    {
        var traveler = await Required(travelerId, cancellationToken);
        var name = newName.Trim();
        if (name.Length == 0 || name == traveler.DisplayName)
        {
            return;
        }

        var oldName = traveler.DisplayName;
        traveler.DisplayName = name;
        traveler.AddAlias(oldName); // keep matching tickets printed with the old name (after the rename, or it counts as "known")
        await SaveAsync(traveler, cancellationToken);
    }

    public async Task LearnAliasAsync(Traveler traveler, string rawName, CancellationToken cancellationToken = default)
    {
        if (traveler.AddAlias(rawName))
        {
            await SaveAsync(traveler, cancellationToken);
        }
    }

    public async Task RemoveAliasAsync(string travelerId, string alias, CancellationToken cancellationToken = default)
    {
        var traveler = await Required(travelerId, cancellationToken);
        if (traveler.Aliases.Remove(alias))
        {
            await SaveAsync(traveler, cancellationToken);
        }
    }

    public async Task LinkMemberAsync(string travelerId, string? memberId, CancellationToken cancellationToken = default)
    {
        var traveler = await Required(travelerId, cancellationToken);
        traveler.LinkedMemberId = memberId;
        await SaveAsync(traveler, cancellationToken);
    }

    public async Task DeleteAsync(string travelerId, CancellationToken cancellationToken = default)
    {
        var traveler = await Required(travelerId, cancellationToken);
        traveler.IsDeleted = true;
        await SaveAsync(traveler, cancellationToken);
    }

    private async Task<Traveler> Required(string id, CancellationToken cancellationToken) =>
        await travelers.GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("Traveler not found.");

    private async Task SaveAsync(Traveler traveler, CancellationToken cancellationToken)
    {
        session.Stamp(traveler);
        await travelers.SaveAsync(traveler, cancellationToken);
        await events.PublishAsync(new VaultChange(traveler.FamilyId, RecordType.Traveler, traveler.Id), cancellationToken);
    }
}
