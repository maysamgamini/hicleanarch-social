using Social.BuildingBlocks.Application;

namespace Relationships.Contracts;

/// <summary>
/// One page of an account's followers, in follower-id order, after <paramref name="After"/>: the
/// fanout reads an audience a page at a time, each page an index range scan.
/// </summary>
public sealed record GetFollowersPage(Guid AccountId, Guid? After, int Limit) : IQuery<FollowersPage>;

/// <summary><paramref name="Next"/> is the cursor of the following page, or null after the last one.</summary>
public sealed record FollowersPage(IReadOnlyList<Guid> Followers, Guid? Next);

/// <summary>Every account this one follows (at most 5,000).</summary>
public sealed record GetFollowing(Guid AccountId) : IQuery<IReadOnlyList<Guid>>;

public sealed record GetCounts(Guid AccountId) : IQuery<RelationshipCounts>;

public sealed record RelationshipCounts(long Followers, long Following);

/// <summary>
/// The authors a viewer must not see: the accounts the viewer blocked, the accounts that blocked the
/// viewer, and the accounts the viewer muted. The feed's read filter asks it for every page.
/// </summary>
public sealed record GetHiddenAuthors(Guid ViewerId) : IQuery<HiddenAuthors>;

public sealed record HiddenAuthors(IReadOnlyList<Guid> AccountIds);

/// <summary>The follower and following counts of many accounts at once; an account with no row counts zero.</summary>
public sealed record GetCountsOf(IReadOnlyList<Guid> AccountIds) : IQuery<IReadOnlyList<AccountCounts>>;

public sealed record AccountCounts(Guid AccountId, long Followers, long Following);

/// <summary>
/// Every pair an account belongs to, as it stands, with the pair's version: what a projection needs to
/// re-project the account. A relationship that is not there is reported off.
/// </summary>
public sealed record GetPairsOf(Guid AccountId) : IQuery<IReadOnlyList<PairState>>;

/// <summary>One direction of one kind of relationship between two accounts. Kind: Follow, Block or Mute.</summary>
public sealed record PairState(string Kind, Guid From, Guid To, bool On, long PairVersion);
