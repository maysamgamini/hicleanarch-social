using Accounts.Contracts;

using Recommendations.Application;
using Recommendations.Domain;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

namespace Recommendations.Infrastructure;

/// <summary>
/// Every Relationships and Accounts event, turned into a change of the derived graph. Versioned edges
/// make each one safe to repeat or reorder, so the module needs no inbox.
/// </summary>
internal sealed class GraphProjectionHandlers(IDispatcher dispatcher) :
    IIntegrationEventHandler<AccountRegistered>, IIntegrationEventHandler<ProfileUpdated>, IIntegrationEventHandler<AccountDeactivated>,
    IIntegrationEventHandler<FollowStarted>, IIntegrationEventHandler<FollowEnded>,
    IIntegrationEventHandler<AccountBlocked>, IIntegrationEventHandler<AccountUnblocked>,
    IIntegrationEventHandler<AccountMuted>, IIntegrationEventHandler<AccountUnmuted>
{
    public Task Handle(AccountRegistered e, CancellationToken ct) => Send(new ProjectAccount(e.AccountId, e.Handle), ct);

    public Task Handle(ProfileUpdated e, CancellationToken ct) => Send(new ProjectAccount(e.AccountId, e.Handle), ct);

    public Task Handle(AccountDeactivated e, CancellationToken ct) => Send(new ProjectDeactivation(e.AccountId), ct);

    public Task Handle(FollowStarted e, CancellationToken ct) => Edge(EdgeKind.Follows, e.FollowerId, e.FolloweeId, e.PairVersion, true, e.OccurredAt, ct);

    public Task Handle(FollowEnded e, CancellationToken ct) => Edge(EdgeKind.Follows, e.FollowerId, e.FolloweeId, e.PairVersion, false, e.OccurredAt, ct);

    public Task Handle(AccountBlocked e, CancellationToken ct) => Edge(EdgeKind.Blocks, e.BlockerId, e.BlockedId, e.PairVersion, true, e.OccurredAt, ct);

    public Task Handle(AccountUnblocked e, CancellationToken ct) => Edge(EdgeKind.Blocks, e.BlockerId, e.BlockedId, e.PairVersion, false, e.OccurredAt, ct);

    public Task Handle(AccountMuted e, CancellationToken ct) => Edge(EdgeKind.Mutes, e.MuterId, e.MutedId, e.PairVersion, true, e.OccurredAt, ct);

    public Task Handle(AccountUnmuted e, CancellationToken ct) => Edge(EdgeKind.Mutes, e.MuterId, e.MutedId, e.PairVersion, false, e.OccurredAt, ct);

    private Task Edge(EdgeKind kind, Guid from, Guid to, long version, bool on, DateTimeOffset since, CancellationToken ct) =>
        Send(new ProjectEdge(new EdgeChange(kind, from, to, version, on, since)), ct);

    private async Task Send<TCommand>(TCommand command, CancellationToken ct)
        where TCommand : ICommand<bool>
    {
        var result = await dispatcher.Send<TCommand, bool>(command, ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Errors[0].Code);
        }
    }
}
