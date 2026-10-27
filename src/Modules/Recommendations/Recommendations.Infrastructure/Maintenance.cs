using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Recommendations.Application;
using Recommendations.Domain;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

namespace Recommendations.Infrastructure;

/// <summary>Relationships answers the reconciliation in process, through its contract.</summary>
internal sealed class InProcessRelationshipSource(IDispatcher dispatcher) : IRelationshipSource
{
    public async Task<IReadOnlyDictionary<Guid, (long Followers, long Following)>> Counts(IReadOnlyList<Guid> accounts, CancellationToken cancellationToken)
    {
        var counts = await dispatcher.Query<GetCountsOf, IReadOnlyList<AccountCounts>>(new GetCountsOf(accounts), cancellationToken);
        return counts.IsSuccess
            ? counts.Value.ToDictionary(c => c.AccountId, c => (c.Followers, c.Following))
            : throw new InvalidOperationException(counts.Errors[0].Code);
    }

    public async Task<IReadOnlyList<EdgeChange>> PairsOf(Guid account, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var pairs = await dispatcher.Query<GetPairsOf, IReadOnlyList<PairState>>(new GetPairsOf(account), cancellationToken);
        return pairs.IsSuccess
            ? [.. pairs.Value.Select(p => new EdgeChange(p.Kind switch { "Follow" => EdgeKind.Follows, "Block" => EdgeKind.Blocks, _ => EdgeKind.Mutes },
                p.From, p.To, p.PairVersion, p.On, at))]
            : throw new InvalidOperationException(pairs.Errors[0].Code);
    }
}

/// <summary>
/// The graph's nightly jobs: the reconciliation, then the compaction. Every task runs them; two tasks
/// doing the same night's work do no harm (versioned applies, recounts and deletes are all repeatable).
/// </summary>
internal sealed partial class GraphMaintenance(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<GraphMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(configuration.GetValue("Recommendations:Maintenance:Every", TimeSpan.FromHours(24)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
                var report = (await dispatcher.Send<Reconcile, ReconciliationReport>(new Reconcile(), stoppingToken)).Value;
                var compacted = (await dispatcher.Send<Compact, long>(new Compact(), stoppingToken)).Value;
                LogDone(logger, report.Checked, report.Repaired, report.Duplicates.Count, compacted);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph maintenance: {Checked} accounts checked, {Repaired} repaired, {Duplicates} duplicate ids, {Compacted} off edges deleted")]
    private static partial void LogDone(ILogger logger, long @checked, long repaired, int duplicates, long compacted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graph maintenance failed; the next night tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
