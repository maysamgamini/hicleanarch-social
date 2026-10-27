using Relationships.Domain;

namespace Relationships.Application;

/// <summary>The module's name, which keys its transaction scope (and, from s2-l06, its outbox).</summary>
public static class ModuleName
{
    public const string Value = "Relationships";
}

/// <summary>
/// Where follows, blocks and mutes are kept. Every change runs inside one transaction of the module
/// and starts with <see cref="LockPair"/>: the pair's version row is bumped, which locks it until
/// commit, so two changes to the same pair always run one after the other.
/// </summary>
public interface IRelationshipStore
{
    /// <summary>Bumps and locks the pair's version, then reads everything between the two accounts.</summary>
    Task<Pair> LockPair(Guid first, Guid second, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the follow and both counts in the same transaction. False, and nothing written, when the
    /// follower already follows <see cref="Pair.MaxFollowing"/> accounts.
    /// </summary>
    Task<bool> AddFollow(Guid follower, Guid followee, CancellationToken cancellationToken);

    /// <summary>Removes the follow and takes it off both counts.</summary>
    Task RemoveFollow(Guid follower, Guid followee, CancellationToken cancellationToken);

    Task Add(EdgeKind kind, Guid from, Guid to, CancellationToken cancellationToken);

    Task Remove(EdgeKind kind, Guid from, Guid to, CancellationToken cancellationToken);
}

/// <summary>An account listed in a relationship page, and since when.</summary>
public sealed record ListedAccount(Guid AccountId, DateTimeOffset Since);

/// <summary>One page, in account-id order; <paramref name="Next"/> is the last id when more may follow.</summary>
public sealed record RelationshipPage(IReadOnlyList<ListedAccount> Items, Guid? Next);

/// <summary>
/// Reads that change nothing and take no lock. They read the writer, unlike Posts' and Accounts' read
/// ports (s3-l11): a block, a mute or an unfollow must hide the author on the very next page.
/// </summary>
public interface IRelationshipReads
{
    Task<RelationshipPage> Followers(Guid account, Guid? after, int limit, CancellationToken cancellationToken);

    Task<RelationshipPage> Following(Guid account, Guid? after, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> AllFollowing(Guid account, CancellationToken cancellationToken);

    Task<(long Followers, long Following)> Counts(Guid account, CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid Account, long Followers, long Following)>> CountsOf(IReadOnlyList<Guid> accounts, CancellationToken cancellationToken);

    /// <summary>Every pair the account is in (from pair_versions), with each kind and direction on or off.</summary>
    Task<IReadOnlyList<(string Kind, Guid From, Guid To, bool On, long Version)>> PairsOf(Guid account, CancellationToken cancellationToken);

    /// <summary>Blocked by the viewer, blocking the viewer, or muted by the viewer.</summary>
    Task<IReadOnlyList<Guid>> HiddenFrom(Guid viewer, CancellationToken cancellationToken);

    /// <summary>Pairs in (low, high) order after the given one, with their versions and the edges that are on.</summary>
    Task<IReadOnlyList<(Guid Low, Guid High, long Version, IReadOnlyList<(string Kind, Guid From, Guid To, DateTimeOffset Since)> Edges)>> PairsPage(
        (Guid Low, Guid High)? after, int limit, CancellationToken cancellationToken);
}

/// <summary>Whether an account exists and is active. Accounts answers it; this module never reads its tables.</summary>
public interface IAccountDirectory
{
    Task<bool> IsActive(Guid account, CancellationToken cancellationToken);
}

/// <summary>
/// The follower-count ledger: follows and unfollows append +1 or -1 for the followee, and a worker
/// folds them into the counts every second.
/// </summary>
public interface IFollowerLedger
{
    /// <summary>Folds up to a batch of ledger rows into the counts; the accounts changed, with their new counts.</summary>
    Task<IReadOnlyList<(Guid Account, long Followers)>> Fold(int batch, CancellationToken cancellationToken);

    /// <summary>Accounts whose counts, with the ledger rows not folded yet, differ from their follows rows.</summary>
    Task<long> Mismatches(CancellationToken cancellationToken);
}
