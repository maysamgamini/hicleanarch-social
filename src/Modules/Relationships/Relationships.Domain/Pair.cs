namespace Relationships.Domain;

/// <summary>What one account has towards another: a follow, a block or a mute. Each has a direction.</summary>
public enum EdgeKind
{
    Follow,
    Block,
    Mute,
}

/// <summary>One relationship of a pair, from one account to the other.</summary>
public sealed record Edge(EdgeKind Kind, Guid From, Guid To);

/// <summary>What a change did: something new, something removed, or nothing because it was already so.</summary>
public enum Change
{
    Unchanged,
    Added,
    Removed,
}

/// <summary>
/// Everything between two accounts, read under the pair's lock, and the version this change gives the
/// pair. Every rule about a pair is decided here, on a state no other change can alter until commit.
/// </summary>
public sealed class Pair(long version, IReadOnlyCollection<Edge> edges)
{
    public const int MaxFollowing = 5_000;

    /// <summary>The pair's version after this change: every change to a pair gets the next number.</summary>
    public long Version { get; } = version;

    public bool Has(EdgeKind kind, Guid from, Guid to) => edges.Contains(new Edge(kind, from, to));

    public Change Follow(Guid follower, Guid followee) =>
        Has(EdgeKind.Follow, follower, followee) ? Change.Unchanged : Change.Added;

    public Change Unfollow(Guid follower, Guid followee) =>
        Has(EdgeKind.Follow, follower, followee) ? Change.Removed : Change.Unchanged;
}
