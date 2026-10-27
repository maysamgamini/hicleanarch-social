using Recommendations.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Recommendations.Application;

/// <summary>
/// The nightly reconciliation: each account's degrees in the graph against Relationships' counts.
/// An account that differs has every one of its pairs re-projected from the source (a versioned
/// apply, so it changes only what is behind) and its degrees recounted from its edges. Duplicate
/// Account nodes are reported. The graph is a projection: it is repaired, never trusted.
/// </summary>
public sealed record Reconcile : ICommand<ReconciliationReport>;

public sealed record ReconciliationReport(long Checked, long Repaired, IReadOnlyList<Guid> Duplicates);

/// <summary>The compaction: edges off for longer than <see cref="Keep"/> are deleted. 30 days outlasts any redelivery.</summary>
public sealed record Compact : ICommand<long>
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
}

public sealed class MaintenanceHandlers(IGraphMaintenance graph, IGraphProjection projection, IRelationshipSource source, TimeProvider clock) :
    ICommandHandler<Reconcile, ReconciliationReport>, ICommandHandler<Compact, long>
{
    public const int Batch = 1_000;

    public async Task<Result<ReconciliationReport>> Handle(Reconcile command, CancellationToken cancellationToken)
    {
        long checkedCount = 0, repaired = 0;
        Guid? after = null;
        while (true)
        {
            var page = await graph.Accounts(after, Batch, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            var truth = await source.Counts([.. page.Select(a => a.AccountId)], cancellationToken);
            foreach (var account in page)
            {
                checkedCount++;
                var counts = truth.TryGetValue(account.AccountId, out var c) ? c : (0, 0);
                if (counts != (account.Followers, account.Following))
                {
                    foreach (var change in await source.PairsOf(account.AccountId, clock.GetUtcNow(), cancellationToken))
                    {
                        await projection.Apply(change, cancellationToken);
                    }

                    await graph.RecountDegrees(account.AccountId, cancellationToken);
                    repaired++;
                }
            }

            after = page[^1].AccountId;
        }

        return Result.Success(new ReconciliationReport(checkedCount, repaired, await graph.DuplicateAccounts(cancellationToken)));
    }

    public async Task<Result<long>> Handle(Compact command, CancellationToken cancellationToken) =>
        Result.Success(await graph.CompactOffEdges(clock.GetUtcNow() - Compact.Keep, cancellationToken));
}
