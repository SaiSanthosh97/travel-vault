namespace TravelVault.Core.Travelers;

public enum MatchStrength
{
    None = 0,

    /// <summary>Initials only ("R SHARMA") or one small spelling difference: suggest, ask the user.</summary>
    Weak = 1,

    /// <summary>Same words, one has an extra middle name/initial: accept, but show it.</summary>
    Good = 2,

    /// <summary>The same name (after normalising) as the profile or one of its aliases.</summary>
    Exact = 3,
}

public sealed record PassengerMatch(string RawName, Traveler? Traveler, MatchStrength Strength)
{
    public bool IsConfident => Strength >= MatchStrength.Good;
}

/// <summary>
/// Finds which traveler a printed passenger name belongs to. Rules, strongest first:
/// exact normalised name → same set of words → one name's words contain the other's (middle names) →
/// initials ("R SHARMA" ↔ "RAHUL SHARMA") or a typo per word (edit distance 1, or 2 for long words).
/// If two travelers match equally well, nothing is chosen: guessing wrong is worse than asking.
/// </summary>
public static class PassengerMatcher
{
    public static PassengerMatch Match(string rawName, IReadOnlyList<Traveler> travelers)
    {
        var name = PersonName.Parse(rawName);
        if (name.IsEmpty)
        {
            return new PassengerMatch(rawName, null, MatchStrength.None);
        }

        var scored = travelers
            .Where(t => !t.IsDeleted)
            .Select(t => (Traveler: t, Strength: t.Names().Select(n => Compare(name, n)).DefaultIfEmpty(MatchStrength.None).Max()))
            .Where(x => x.Strength > MatchStrength.None)
            .OrderByDescending(x => x.Strength)
            .ToList();

        if (scored.Count == 0)
        {
            return new PassengerMatch(rawName, null, MatchStrength.None);
        }

        if (scored.Count > 1 && scored[1].Strength == scored[0].Strength)
        {
            return new PassengerMatch(rawName, null, MatchStrength.None); // ambiguous
        }

        return new PassengerMatch(rawName, scored[0].Traveler, scored[0].Strength);
    }

    internal static MatchStrength Compare(PersonName printed, PersonName known)
    {
        if (printed.Equals(known))
        {
            return MatchStrength.Exact;
        }

        var a = printed.Tokens.ToHashSet();
        var b = known.Tokens.ToHashSet();
        if (a.SetEquals(b))
        {
            return MatchStrength.Exact; // "SHARMA RAHUL" vs "RAHUL SHARMA"
        }

        // Extra middle name/initial on one side: "RAHUL K SHARMA" vs "RAHUL SHARMA". Needs two full words in common.
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        if (small.Count >= 2 && small.IsSubsetOf(large) && small.Count(t => t.Length > 1) >= 2)
        {
            return MatchStrength.Good;
        }

        return TokensCorrespond(printed.Tokens, known.Tokens) ? MatchStrength.Weak : MatchStrength.None;
    }

    /// <summary>
    /// Every word of the shorter name corresponds to a different word of the longer one, by initial
    /// ("R" ↔ "RAHUL") or small typo. At least one full word must match exactly (so "R S" alone never matches).
    /// </summary>
    private static bool TokensCorrespond(IReadOnlyList<string> x, IReadOnlyList<string> y)
    {
        var (shortList, longList) = x.Count <= y.Count ? (x, y) : (y, x);
        if (shortList.Count < 2 && longList.Count >= 2)
        {
            return false; // a single word can't identify a person
        }

        var used = new bool[longList.Count];
        var exactWords = 0;
        foreach (var token in shortList)
        {
            var found = -1;
            for (var i = 0; i < longList.Count && found < 0; i++)
            {
                if (!used[i] && longList[i] == token)
                {
                    found = i;
                    exactWords++;
                }
            }

            for (var i = 0; i < longList.Count && found < 0; i++)
            {
                if (!used[i] && WordsCorrespond(token, longList[i]))
                {
                    found = i;
                }
            }

            if (found < 0)
            {
                return false;
            }

            used[found] = true;
        }

        return exactWords >= 1;
    }

    private static bool WordsCorrespond(string a, string b)
    {
        if (a.Length == 1 || b.Length == 1)
        {
            return a[0] == b[0];
        }

        var allowed = Math.Min(a.Length, b.Length) >= 7 ? 2 : 1;
        return Math.Abs(a.Length - b.Length) <= allowed && EditDistance(a, b) <= allowed;
    }

    internal static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
