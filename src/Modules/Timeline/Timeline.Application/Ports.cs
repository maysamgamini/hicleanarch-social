namespace Timeline.Application;

/// <summary>The module's name, which keys its handlers.</summary>
public static class ModuleName
{
    public const string Value = "Timeline";
}

/// <summary>Where timelines are kept. Every method is safe to repeat.</summary>
public interface ITimelines
{
    /// <summary>
    /// Adds the post to the timeline of each owner whose timeline exists, and trims each to its
    /// newest entries; a missing timeline is left missing. Returns how many timelines took it.
    /// </summary>
    Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken);
}

/// <summary>An author's audience, a page at a time, in follower-id order.</summary>
public interface IFollowerPages
{
    Task<(IReadOnlyList<Guid> Followers, Guid? Next)> Page(Guid author, Guid? after, int limit, CancellationToken cancellationToken);
}
