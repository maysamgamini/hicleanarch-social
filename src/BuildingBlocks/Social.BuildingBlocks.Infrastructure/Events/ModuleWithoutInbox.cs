using Microsoft.Extensions.DependencyInjection;

namespace Social.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// A module whose handlers are idempotent by construction (adding the same member with the same
/// score twice changes nothing) needs no inbox, and one that publishes nothing needs no outbox.
/// Its handlers run in a fresh scope, with no transaction of their own.
/// </summary>
internal sealed class ModuleWithoutInbox(string module, IServiceScopeFactory scopes) : IModuleEvents
{
    public string Module => module;

    public Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

    public Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);

    public Task MarkDelivered(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordFailure(Guid eventId, string error, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await handle(scope.ServiceProvider, cancellationToken);
    }
}

public static class ModuleWithoutInboxRegistration
{
    /// <summary>Registers <paramref name="module"/> as one whose handlers need no inbox: each is idempotent by itself.</summary>
    public static IServiceCollection AddModuleWithoutInbox(this IServiceCollection services, string module) =>
        services.AddSingleton<IModuleEvents>(sp => new ModuleWithoutInbox(module, sp.GetRequiredService<IServiceScopeFactory>()));
}
