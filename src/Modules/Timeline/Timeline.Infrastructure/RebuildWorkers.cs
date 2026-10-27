using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Valkey;

using Timeline.Application;

namespace Timeline.Infrastructure;

/// <summary>The rebuild lock: <c>rebuild:{reader}</c>, SET NX EX 60, deleted when the rebuild ends; in the short-lived store.</summary>
internal sealed class ValkeyRebuildLock([FromKeyedServices(ValkeyRegistration.ShortLived)] IConnectionMultiplexer valkey) : IRebuildLock
{
    public static RedisKey Key(Guid reader) => "rebuild:" + reader.ToString("N");

    public Task<bool> TryTake(Guid reader, TimeSpan hold, CancellationToken cancellationToken) =>
        valkey.GetDatabase().StringSetAsync(Key(reader), 1, hold, When.NotExists).WaitAsync(cancellationToken);

    public Task Release(Guid reader, CancellationToken cancellationToken) =>
        valkey.GetDatabase().KeyDeleteAsync(Key(reader)).WaitAsync(cancellationToken);
}

/// <summary>Timeline's IAuthorActivity answered in process by Posts' GetRecentlyActiveAuthors.</summary>
internal sealed class InProcessAuthorActivity(IDispatcher dispatcher) : IAuthorActivity
{
    public async Task<IReadOnlyList<(Guid Author, DateTimeOffset LastPostedAt)>> MostRecent(IReadOnlyList<Guid> authors, int limit, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Query<Posts.Contracts.GetRecentlyActiveAuthors, IReadOnlyList<Posts.Contracts.AuthorActivity>>(
            new Posts.Contracts.GetRecentlyActiveAuthors(authors, limit), cancellationToken);
        return result.IsSuccess
            ? [.. result.Value.Select(a => (a.AuthorId, a.LastPostedAt))]
            : throw new InvalidOperationException($"Posts refused a read: {result.Errors[0].Code}");
    }
}

/// <summary>
/// The rebuild loops on worker tasks (Timeline:Rebuild:Workers, 2): each claims a job, builds the
/// timeline under the lock and claims the next; with nothing to do it waits a tenth of a second.
/// </summary>
internal sealed partial class RebuildWorkers(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<RebuildWorkers> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(100);

    /// <summary>A job not answered within this is taken up again by another loop.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, configuration.GetValue("Timeline:Rebuild:Workers", 2)).Select(_ => Loop(stoppingToken)));

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var worked = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<RunRebuild, bool>(new RunRebuild(Lease), ct);
                if (!worked.IsSuccess || !worked.Value)
                {
                    await Task.Delay(Idle, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogFailed(logger, e); // the job's lease runs out and another loop takes it
                await Task.Delay(Idle, CancellationToken.None);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A timeline rebuild failed; its job is taken up again when its lease runs out")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
