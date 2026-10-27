using System.Collections.Concurrent;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Social.BuildingBlocks.Infrastructure.Hosting;

using Timeline.Application;
using Timeline.Domain;

namespace Timeline.Infrastructure;

/// <summary>
/// The fanout's worker loops: a fixed number per pool (Timeline:Fanout:Workers:Small and :Large), so a
/// flood of large jobs can only ever occupy the large pool's loops. Each loop claims a job, writes it to
/// its end and claims the next; with nothing to do it waits a quarter of a second. Stopping, a task
/// gives back the jobs it holds at once instead of letting their leases run out.
/// </summary>
internal sealed partial class FanoutWorkers(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<FanoutWorkers> logger)
    : BackgroundService, IStopParticipant
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<Guid, FanoutJob> _held = new();
    private readonly CancellationTokenSource _stopping = new();

    public string Name => "fanout workers";

    private TimeSpan Lease => configuration.GetValue("Timeline:Fanout:Lease", FanoutRunner.DefaultLease);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var small = configuration.GetValue("Timeline:Fanout:Workers:Small", 4);
        var large = configuration.GetValue("Timeline:Fanout:Workers:Large", 2);
        return Task.WhenAll(Enumerable.Repeat(FanoutPool.Small, small).Concat(Enumerable.Repeat(FanoutPool.Large, large))
            .Select(pool => Loop(pool, stoppingToken)));
    }

    private async Task Loop(FanoutPool pool, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stopping.Token);
        var ct = linked.Token;
        FanoutPace.CurrentPool.Value = pool;
        while (!ct.IsCancellationRequested)
        {
            FanoutJob? current = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<FanoutRunner>();
                var worked = await runner.RunOne(pool, Lease, job => _held[job.PostId] = current = job, ct);
                if (current is not null)
                {
                    _held.TryRemove(current.PostId, out _);
                }

                if (!worked)
                {
                    await Task.Delay(Idle, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // the held job, if any, is given back by Stop
            }
            catch (Exception e)
            {
                // A failure this task saw (Valkey or Relationships unreachable, say) gives the job back at
                // once, to be resumed from its cursor; only a task that dies leaves it to its lease.
                if (current is not null && _held.TryRemove(current.PostId, out var held))
                {
                    await ReleaseQuietly(held);
                }

                LogFailed(logger, e, pool);
                await Task.Delay(Idle, CancellationToken.None);
            }
        }
    }

    private async Task ReleaseQuietly(FanoutJob job)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IFanoutJobs>().Release(job, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e, job.Pool); // the lease runs out on its own
        }
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IFanoutJobs>();
        foreach (var job in _held.Values)
        {
            await jobs.Release(job, cancellationToken);
        }
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Pool} fanout job failed; it is resumed from its cursor")]
    private static partial void LogFailed(ILogger logger, Exception exception, FanoutPool pool);
}
