namespace Timeline.Domain;

/// <summary>
/// Fanout jobs run in two pools by audience size, each with its own workers, so a flood of posts by
/// large accounts cannot hold back the posts of everyone else.
/// </summary>
public enum FanoutPool
{
    Small,
    Large,
}

/// <summary>What a fanout job writes: a post into the timelines, or (after PostDeleted) its removal from them.</summary>
public enum FanoutKind
{
    Add,
    Remove,
}

public static class FanoutPools
{
    /// <summary>An audience of 1,000 followers or more (one page or more) goes to the large pool.</summary>
    public const int LargeFrom = 1_000;

    /// <summary>Followers written per page: one page is the unit a job advances by.</summary>
    public const int PageSize = 1_000;

    public static FanoutPool For(long audience) => audience < LargeFrom ? FanoutPool.Small : FanoutPool.Large;
}
