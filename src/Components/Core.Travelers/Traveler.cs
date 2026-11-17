using TravelVault.Core.Common;

namespace TravelVault.Core.Travelers;

/// <summary>
/// A person who travels: a family member, or someone without the app (a child, a grandparent).
/// Aliases are the other ways the name appears on tickets ("SHARMA/RAHUL", "R K SHARMA"); they are learned when a
/// user confirms a match, so the next ticket matches automatically.
/// </summary>
public sealed class Traveler : IVersioned
{
    public required string Id { get; init; }

    public required string FamilyId { get; init; }

    public required string DisplayName { get; set; }

    /// <summary>Raw alias strings as printed. Matching uses their normalised form.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>The member (device) this traveler is, if any, so that member gets their own reminders.</summary>
    public string? LinkedMemberId { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public HybridTimestamp Version { get; set; }

    public string UpdatedBy { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public static Traveler Create(string familyId, string displayName, string? linkedMemberId = null)
    {
        var name = displayName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A traveler needs a name.", nameof(displayName));
        }

        return new Traveler { Id = Ids.New(), FamilyId = familyId, DisplayName = name, LinkedMemberId = linkedMemberId };
    }

    /// <summary>All the names this traveler is known by, normalised.</summary>
    public IEnumerable<PersonName> Names() =>
        new[] { DisplayName }.Concat(Aliases).Select(PersonName.Parse).Where(n => !n.IsEmpty).Distinct();

    /// <summary>Remembers another printed form of the name (no-op if it normalises to a known one).</summary>
    public bool AddAlias(string raw)
    {
        var name = PersonName.Parse(raw);
        if (name.IsEmpty || Names().Contains(name))
        {
            return false;
        }

        Aliases.Add(raw.Trim());
        return true;
    }
}
