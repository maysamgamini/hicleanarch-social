using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Accounts.UnitTests;

/// <summary>Runs the work as it is: the store in memory has no transaction to open.</summary>
internal sealed class NoTransaction : ITransactionScope
{
    public Task<Result<T>> Run<T>(Func<CancellationToken, Task<Result<T>>> work, CancellationToken cancellationToken) => work(cancellationToken);
}

/// <summary>Keeps the events a use case wrote, so a test can read them.</summary>
internal sealed class RecordingOutbox : IOutbox
{
    public List<IntegrationEvent> Events { get; } = [];

    public Task Add(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        Events.Add(integrationEvent);
        return Task.CompletedTask;
    }

    public Task AddRange(IReadOnlyCollection<IntegrationEvent> integrationEvents, CancellationToken cancellationToken)
    {
        Events.AddRange(integrationEvents);
        return Task.CompletedTask;
    }
}
