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

/// <summary>Reads that change nothing and take no lock.</summary>
public interface IRelationshipReads
{
    Task<RelationshipPage> Followers(Guid account, Guid? after, int limit, CancellationToken cancellationToken);

    Task<RelationshipPage> Following(Guid account, Guid? after, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> AllFollowing(Guid account, CancellationToken cancellationToken);

    Task<(long Followers, long Following)> Counts(Guid account, CancellationToken cancellationToken);

    /// <summary>Blocked by the viewer, blocking the viewer, or muted by the viewer.</summary>
    Task<IReadOnlyList<Guid>> HiddenFrom(Guid viewer, CancellationToken cancellationToken);
}

/// <summary>Whether an account exists and is active. Accounts answers it; this module never reads its tables.</summary>
public interface IAccountDirectory
{
    Task<bool> IsActive(Guid account, CancellationToken cancellationToken);
}
