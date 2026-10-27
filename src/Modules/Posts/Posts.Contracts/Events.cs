using Social.BuildingBlocks.Application;

namespace Posts.Contracts;

// The post events carry the post's state, so a consumer that keeps a copy (Timeline, in Stage 5)
// never needs to ask Posts anything back.

/// <summary>A post was stored. Written in the same transaction as the post and its idempotency key.</summary>
public sealed record PostPublished(Guid EventId, DateTimeOffset OccurredAt, Guid PostId, Guid AuthorId, DateTimeOffset CreatedAt, string Text)
    : IntegrationEvent(EventId, OccurredAt);

public sealed record PostEdited(Guid EventId, DateTimeOffset OccurredAt, Guid PostId, Guid AuthorId, DateTimeOffset EditedAt, string Text)
    : IntegrationEvent(EventId, OccurredAt);

public sealed record PostDeleted(Guid EventId, DateTimeOffset OccurredAt, Guid PostId, Guid AuthorId, DateTimeOffset DeletedAt)
    : IntegrationEvent(EventId, OccurredAt);
