using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Relationships.Application;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;

namespace Relationships.Infrastructure;

/// <summary>
/// The counts aggregator, on worker tasks: every second it folds the ledger into the counts and
/// announces each changed account once. Every task may run it: SKIP LOCKED gives each a different batch.
/// </summary>
internal sealed class CountsAggregator(IServiceScopeFactory scopes, ILogger<CountsAggregator> logger) : ModuleMonitor(scopes, logger)
{
    protected override TimeSpan Every => TimeSpan.FromSeconds(1);

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken) =>
        await services.GetRequiredService<IDispatcher>().Send<FoldFollowerCounts, int>(new FoldFollowerCounts(), cancellationToken);
}

/// <summary>
/// The nightly check (Relationships:CountsCheck:Every, 24 hours): accounts whose counts disagree with
/// their follows rows, as the gauge social.invariant.count_mismatches, which must read zero.
/// </summary>
internal sealed class CountsCheck(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CountsCheck> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured Mismatches = new Operations.Measured(
        "social.invariant.count_mismatches", "{account}", "Accounts whose counts disagree with their follows: must be 0").Register();

    protected override TimeSpan Every => configuration.GetValue("Relationships:CountsCheck:Every", TimeSpan.FromHours(24));

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken) =>
        await MeasureOnce(services, cancellationToken);

    public static async Task<long> MeasureOnce(IServiceProvider services, CancellationToken cancellationToken)
    {
        var mismatches = (await services.GetRequiredService<IDispatcher>().Query<CheckCounts, long>(new CheckCounts(), cancellationToken)).Value;
        Mismatches.Set(mismatches);
        return mismatches;
    }
}
