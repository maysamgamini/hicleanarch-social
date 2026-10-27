using Recommendations.Domain;

namespace Recommendations.Application;

/// <summary>The module's name, which keys its handlers.</summary>
public static class ModuleName
{
    public const string Value = "Recommendations";
}

/// <summary>Writes the derived graph. Every method may run twice or out of order with no harm.</summary>
public interface IGraphProjection
{
    /// <summary>Creates the account or updates its handle; never creates it twice.</summary>
    Task UpsertAccount(Guid accountId, string handle, CancellationToken cancellationToken);

    Task Deactivate(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the change only if its version is higher than the edge's. True when it was applied.
    /// Fails if either account is not in the graph yet: delivery retries it once it is.
    /// </summary>
    Task<bool> Apply(EdgeChange change, CancellationToken cancellationToken);

    /// <summary>Hides <paramref name="account"/> from the reader's suggestions until then. False if either account is not in the graph.</summary>
    Task<bool> Dismiss(Guid reader, Guid account, DateTimeOffset until, CancellationToken cancellationToken);
}

/// <summary>Walks the derived graph.</summary>
public interface IGraphReads
{
    /// <summary>Friends of friends: accounts the reader's intermediates follow, with how many lead to each.</summary>
    Task<IReadOnlyList<Suggestion>> WhoToFollow(Guid reader, TraversalLimits limits, CancellationToken cancellationToken);

    /// <summary>Friends (mutual follows) of the reader's friends, with how many friends they share.</summary>
    Task<IReadOnlyList<Suggestion>> FriendsOfFriends(Guid reader, TraversalLimits limits, CancellationToken cancellationToken);

    /// <summary>Accounts that follow the reader and are not followed back: the newest <paramref name="limit"/> by since.</summary>
    Task<IReadOnlyList<Suggestion>> FollowersNotFollowedBack(Guid reader, int limit, TraversalLimits limits, CancellationToken cancellationToken);

    /// <summary>Of these accounts, the ones the reader may not be shown now: blocked either way, muted by the reader, or dismissed.</summary>
    Task<IReadOnlySet<Guid>> HiddenNow(Guid reader, IReadOnlyList<Guid> accounts, CancellationToken cancellationToken);
}

/// <summary>Computed lists, kept an hour per reader and kind, so a reader costs one traversal an hour.</summary>
public interface ISuggestionCache
{
    Task<IReadOnlyList<Suggestion>?> Get(Guid reader, string kind, CancellationToken cancellationToken);

    Task Set(Guid reader, string kind, IReadOnlyList<Suggestion> suggestions, CancellationToken cancellationToken);

    /// <summary>Drops both of the reader's lists.</summary>
    Task Drop(Guid reader, CancellationToken cancellationToken);
}

/// <summary>Dismissals, kept durably in Recommendations' own schema: the graph's DISMISSED edges are their projection.</summary>
public interface IDismissals
{
    Task Save(Guid reader, Guid account, DateTimeOffset until, CancellationToken cancellationToken);

    Task<int> PurgeBefore(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>The dismissals of these readers still in force at <paramref name="now"/>: what the reconciliation projects again.</summary>
    Task<IReadOnlyList<(Guid Reader, Guid Account, DateTimeOffset Until)>> InForce(IReadOnlyList<Guid> readers, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>An account's degrees as the graph holds them.</summary>
public sealed record GraphDegrees(Guid AccountId, long Followers, long Following);

/// <summary>The graph's own upkeep: what the nightly jobs read and repair.</summary>
public interface IGraphMaintenance
{
    /// <summary>Accounts in id order after <paramref name="after"/>, with their degree properties.</summary>
    Task<IReadOnlyList<GraphDegrees>> Accounts(Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>Sets the account's degree properties from its edges that are on.</summary>
    Task RecountDegrees(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Ids held by more than one Account node (possible on Neptune, which has no uniqueness constraint).</summary>
    Task<IReadOnlyList<Guid>> DuplicateAccounts(CancellationToken cancellationToken);

    /// <summary>Deletes edges that have been off since before <paramref name="cutoff"/>. Returns how many.</summary>
    Task<long> CompactOffEdges(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Deletes dismissals whose time has passed: the account may be suggested again.</summary>
    Task<long> CompactDismissals(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The source of truth, asked only by the nightly reconciliation: counts, and every pair of an account.</summary>
public interface IRelationshipSource
{
    Task<IReadOnlyDictionary<Guid, (long Followers, long Following)>> Counts(IReadOnlyList<Guid> accounts, CancellationToken cancellationToken);

    Task<IReadOnlyList<EdgeChange>> PairsOf(Guid account, DateTimeOffset at, CancellationToken cancellationToken);
}
