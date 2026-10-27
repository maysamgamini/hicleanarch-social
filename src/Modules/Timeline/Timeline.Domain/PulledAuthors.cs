namespace Timeline.Domain;

/// <summary>
/// Hybrid fanout: most authors are pushed (their posts written into each follower's timeline); an
/// author with very many followers is pulled (their posts read at read time by each follower).
/// </summary>
public static class PullRules
{
    /// <summary>An author is promoted to pulled at 25,000 followers...</summary>
    public const long PromoteAt = 25_000;

    /// <summary>...and demoted below 20,000: the gap keeps an author near the line from flapping.</summary>
    public const long DemoteBelow = 20_000;

    /// <summary>How long a reader's pull list is cached (pulls:{reader}).</summary>
    public static readonly TimeSpan PullListCache = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A promoted author is still pushed for the pull-list cache's 10 minutes plus 2 of margin: a reader
    /// whose cached list does not have the author yet still gets the posts in their timeline.
    /// </summary>
    public static readonly TimeSpan PushAfterPromotion = PullListCache + TimeSpan.FromMinutes(2);

    /// <summary>
    /// A demoted author is still pulled for 3 days, Round 2's timeline expiry: the posts that were not
    /// pushed while the author was pulled are still found until every timeline that lacks them has expired.
    /// </summary>
    public static readonly TimeSpan PullAfterDemotion = TimelineRules.Expiry;

    /// <summary>The time a feed read gives each pulled author's posts; a slower author is skipped for this page.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(25);
}

/// <summary>An author in the registry: promoted at a time, and demoted at a later one if they have been.</summary>
public sealed record PulledAuthor(Guid AuthorId, DateTimeOffset PromotedAt, DateTimeOffset? DemotedAt = null)
{
    /// <summary>Are the author's posts still written into followers' timelines?</summary>
    public bool Pushed(DateTimeOffset now) => DemotedAt is not null || now - PromotedAt < PullRules.PushAfterPromotion;

    /// <summary>Do followers still read the author's posts at read time?</summary>
    public bool Pulled(DateTimeOffset now) => DemotedAt is null || now - DemotedAt < PullRules.PullAfterDemotion;

    /// <summary>
    /// The registry's entry for an author after a new follower count: promoted at 25,000 (again, if they
    /// had been demoted), demoted below 20,000, unchanged in between. Null: not in the registry.
    /// </summary>
    public static PulledAuthor? Next(PulledAuthor? current, Guid author, long followers, DateTimeOffset now) => followers switch
    {
        >= PullRules.PromoteAt when current is null || current.DemotedAt is not null => new PulledAuthor(author, now),
        < PullRules.DemoteBelow when current is { DemotedAt: null } => current with { DemotedAt = now },
        _ => current,
    };
}
