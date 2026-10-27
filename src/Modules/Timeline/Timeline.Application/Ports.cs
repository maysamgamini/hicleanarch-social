namespace Timeline.Application;

/// <summary>The module's name, which keys its handlers.</summary>
public static class ModuleName
{
    public const string Value = "Timeline";
}

/// <summary>One timeline entry: a post id and its score (the post's Unix milliseconds).</summary>
public sealed record Entry(Guid PostId, long Score);

/// <summary>Where timelines are kept. Every method is safe to repeat.</summary>
public interface ITimelines
{
    /// <summary>
    /// Adds the post to the timeline of each owner whose timeline exists, and trims each to its
    /// newest entries; a missing timeline is left missing. Returns how many timelines took it.
    /// </summary>
    Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken);

    Task<bool> Exists(Guid owner, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the owner's timeline holding only the sentinel, with its expiry, unless it exists.
    /// True when this call created it. From then on a fanout finds it and adds to it.
    /// </summary>
    Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken);

    /// <summary>Adds these posts to the owner's timeline if it exists, and trims it.</summary>
    Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken);

    /// <summary>
    /// Entries newest first, at most <paramref name="count"/>, skipping the first <paramref name="skip"/>
    /// of those with a score at most <paramref name="maxScore"/> (all when null). The sentinel is never returned.
    /// </summary>
    Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken);

    /// <summary>Moves the expiry: a timeline lives while it is read.</summary>
    Task Touch(Guid owner, CancellationToken cancellationToken);
}

/// <summary>An author's audience, a page at a time, in follower-id order.</summary>
public interface IFollowerPages
{
    Task<(IReadOnlyList<Guid> Followers, Guid? Next)> Page(Guid author, Guid? after, int limit, CancellationToken cancellationToken);
}

/// <summary>A post as the feed shows it.</summary>
public sealed record PostCard(Guid PostId, Guid AuthorId, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);

/// <summary>Posts as the feed needs them: current text, deleted ones left out.</summary>
public interface IPostSummaries
{
    /// <summary>The active posts among these ids, in one call.</summary>
    Task<IReadOnlyList<PostCard>> Get(IReadOnlyList<Guid> postIds, CancellationToken cancellationToken);

    /// <summary>Each author's newest active posts, at most <paramref name="perAuthor"/> each.</summary>
    Task<IReadOnlyList<PostCard>> RecentByAuthors(IReadOnlyList<Guid> authors, int perAuthor, CancellationToken cancellationToken);
}

/// <summary>An author as the feed shows them.</summary>
public sealed record AuthorCard(Guid AccountId, string Handle, string DisplayName, bool Active);

public interface IProfiles
{
    /// <summary>The profiles of these accounts, in one call.</summary>
    Task<IReadOnlyList<AuthorCard>> Get(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken);
}

/// <summary>The accounts a reader follows.</summary>
public interface IFollowing
{
    Task<IReadOnlyList<Guid>> Of(Guid reader, CancellationToken cancellationToken);
}
