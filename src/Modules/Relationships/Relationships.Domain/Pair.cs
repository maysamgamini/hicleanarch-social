using Social.BuildingBlocks.Domain;

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

    /// <summary>No follow across a block, in either direction.</summary>
    public Result<Change> Follow(Guid follower, Guid followee)
    {
        if (Has(EdgeKind.Block, follower, followee) || Has(EdgeKind.Block, followee, follower))
        {
            return Result.Failure<Change>(RelationshipErrors.Blocked);
        }

        return Result.Success(Has(EdgeKind.Follow, follower, followee) ? Change.Unchanged : Change.Added);
    }

    public Change Unfollow(Guid follower, Guid followee) =>
        Has(EdgeKind.Follow, follower, followee) ? Change.Removed : Change.Unchanged;

    /// <summary>
    /// A block removes the follows in both directions, in the same transaction as the block itself:
    /// a follow and a block of the same two accounts never exist together.
    /// </summary>
    public BlockOutcome Block(Guid blocker, Guid blocked)
    {
        Edge[] follows = [new(EdgeKind.Follow, blocker, blocked), new(EdgeKind.Follow, blocked, blocker)];
        return new BlockOutcome(
            Has(EdgeKind.Block, blocker, blocked) ? Change.Unchanged : Change.Added,
            [.. follows.Where(f => Has(f.Kind, f.From, f.To))]);
    }

    public Change Unblock(Guid blocker, Guid blocked) =>
        Has(EdgeKind.Block, blocker, blocked) ? Change.Removed : Change.Unchanged;

    /// <summary>A mute is one-way and changes no follow: the muter stops seeing the muted account's posts, nothing else.</summary>
    public Change Mute(Guid muter, Guid muted) =>
        Has(EdgeKind.Mute, muter, muted) ? Change.Unchanged : Change.Added;

    public Change Unmute(Guid muter, Guid muted) =>
        Has(EdgeKind.Mute, muter, muted) ? Change.Removed : Change.Unchanged;
}

/// <summary>What a block did: the block itself, and every follow it removed.</summary>
public sealed record BlockOutcome(Change Block, IReadOnlyList<Edge> RemovedFollows);
