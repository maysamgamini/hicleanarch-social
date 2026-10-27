namespace Recommendations.Domain;

/// <summary>What one account has towards another in the derived graph.</summary>
public enum EdgeKind
{
    Follows,
    Blocks,
    Mutes,
}

/// <summary>
/// A versioned edge, as an event reports it. An edge is never deleted by an event: it is turned on or
/// off, and only by an event whose pair version is higher than the one the edge holds, so a late or
/// repeated event changes nothing. <paramref name="Since"/> is when the edge took its state: when the
/// follow (or block, or mute) began, or, for an edge turned off, when it ended.
/// </summary>
public sealed record EdgeChange(EdgeKind Kind, Guid From, Guid To, long Version, bool On, DateTimeOffset Since);

/// <summary>
/// The bounds of a walk: suggestion queries touch the edges they walk in any engine, so the walk is
/// bounded per hop. No supernode (an account with at least <see cref="SupernodeFollowers"/>
/// followers) is ever an intermediate; an intermediate follows at most <see cref="MaxIntermediateFollowing"/>
/// accounts; the first hop takes the reader's <see cref="Intermediates"/> newest follows.
/// </summary>
public sealed record TraversalLimits(int SupernodeFollowers, int MaxIntermediateFollowing, int Intermediates, int Results)
{
    public static readonly TraversalLimits Default = new(10_000, 1_000, 200, 20);
}

/// <summary>A suggested account and how many of the reader's intermediates lead to it.</summary>
public sealed record Suggestion(Guid AccountId, string Handle, long Mutual);

/// <summary>The two suggestion lists, by the name their cache entries and endpoints use.</summary>
public static class SuggestionKind
{
    public const string WhoToFollow = "who-to-follow";
    public const string PeopleYouMayKnow = "people-you-may-know";
}

/// <summary>A dismissal hides an account from the reader's suggestions for 90 days.</summary>
public static class Dismissal
{
    public static readonly TimeSpan Lasts = TimeSpan.FromDays(90);
}

public static class SuggestionRanking
{
    /// <summary>Most mutual connections first; ties by account id in PostgreSQL's uuid order (its bytes, as written).</summary>
    public static IReadOnlyList<Suggestion> Rank(IEnumerable<Suggestion> suggestions, int limit) =>
        [.. suggestions
            .OrderByDescending(s => s.Mutual)
            .ThenBy(s => s.AccountId.ToString("N"), StringComparer.Ordinal)
            .Take(limit)];

    /// <summary>
    /// People you may know: accounts that follow the reader and are not followed back come first,
    /// newest follow first; then friends of friends by mutual friends, ties by id. An account in both
    /// lists appears once, in the first, with its mutual count.
    /// </summary>
    public static IReadOnlyList<Suggestion> PeopleYouMayKnow(IReadOnlyList<Suggestion> followersNotFollowedBack, IEnumerable<Suggestion> friendsOfFriends, int limit)
    {
        var mutual = friendsOfFriends.ToDictionary(s => s.AccountId);
        var first = followersNotFollowedBack.Select(f => mutual.TryGetValue(f.AccountId, out var m) ? f with { Mutual = m.Mutual } : f).ToList();
        var shown = first.Select(f => f.AccountId).ToHashSet();
        return [.. first.Concat(Rank(mutual.Values.Where(m => !shown.Contains(m.AccountId)), limit)).Take(limit)];
    }
}
