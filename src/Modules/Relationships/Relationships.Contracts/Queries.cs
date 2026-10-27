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
