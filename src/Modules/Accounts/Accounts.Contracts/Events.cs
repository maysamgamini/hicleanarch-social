using Social.BuildingBlocks.Application;

namespace Accounts.Contracts;

/// <summary>A person signed in and chose a handle: an account now exists.</summary>
public sealed record AccountRegistered(Guid EventId, DateTimeOffset OccurredAt, Guid AccountId, string Handle, string DisplayName)
    : IntegrationEvent(EventId, OccurredAt);

/// <summary>An account's handle or display name changed; it carries the whole profile, so a copy needs nothing else.</summary>
public sealed record ProfileUpdated(Guid EventId, DateTimeOffset OccurredAt, Guid AccountId, string Handle, string DisplayName)
    : IntegrationEvent(EventId, OccurredAt);

/// <summary>An account is no longer active: its posts leave every feed and it is suggested to nobody.</summary>
public sealed record AccountDeactivated(Guid EventId, DateTimeOffset OccurredAt, Guid AccountId)
    : IntegrationEvent(EventId, OccurredAt);
