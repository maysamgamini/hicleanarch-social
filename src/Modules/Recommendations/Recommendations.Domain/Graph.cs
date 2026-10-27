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

public static class SuggestionRanking
{
    /// <summary>Most mutual connections first; ties by account id in PostgreSQL's uuid order (its bytes, as written).</summary>
    public static IReadOnlyList<Suggestion> Rank(IEnumerable<Suggestion> suggestions, int limit) =>
        [.. suggestions
            .OrderByDescending(s => s.Mutual)
            .ThenBy(s => s.AccountId.ToString("N"), StringComparer.Ordinal)
            .Take(limit)];
}
