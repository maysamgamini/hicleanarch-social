using Social.BuildingBlocks.Application;

namespace Relationships.Contracts;

// Every relationship event carries the pair's version after the change. Events of one pair may be
// delivered more than once and in any order; a consumer applies one only if its version is higher
// than the last it applied for that pair, so the end state is the one of the highest version.

public sealed record FollowStarted(Guid EventId, DateTimeOffset OccurredAt, Guid FollowerId, Guid FolloweeId, long PairVersion)
    : IntegrationEvent(EventId, OccurredAt);

/// <summary>An unfollow, or a follow that a block removed (then with the block's version).</summary>
public sealed record FollowEnded(Guid EventId, DateTimeOffset OccurredAt, Guid FollowerId, Guid FolloweeId, long PairVersion)
    : IntegrationEvent(EventId, OccurredAt);

public sealed record AccountBlocked(Guid EventId, DateTimeOffset OccurredAt, Guid BlockerId, Guid BlockedId, long PairVersion)
    : IntegrationEvent(EventId, OccurredAt);

public sealed record AccountUnblocked(Guid EventId, DateTimeOffset OccurredAt, Guid BlockerId, Guid BlockedId, long PairVersion)
    : IntegrationEvent(EventId, OccurredAt);

public sealed record AccountMuted(Guid EventId, DateTimeOffset OccurredAt, Guid MuterId, Guid MutedId, long PairVersion)
    : IntegrationEvent(EventId, OccurredAt);

public sealed record AccountUnmuted(Guid EventId, DateTimeOffset OccurredAt, Guid MuterId, Guid MutedId, long PairVersion)
    : IntegrationEvent(EventId, OccurredAt);
