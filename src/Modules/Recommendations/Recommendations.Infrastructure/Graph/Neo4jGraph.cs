using Neo4j.Driver;

using Recommendations.Application;
using Recommendations.Domain;

namespace Recommendations.Infrastructure.Graph;

/// <summary>
/// The graph in a graph database, over Bolt with the official Neo4j driver: Neo4j 5.26 locally and
/// in tests, Amazon Neptune on AWS. Writes and reads run as managed transactions, which the driver
/// retries on transient errors (a deadlock between two projections, Neptune's false conflicts).
/// </summary>
internal sealed class Neo4jGraph(IDriver driver) : IGraphProjection, IGraphReads, IGraphMaintenance
{
    public async Task UpsertAccount(Guid accountId, string handle, CancellationToken cancellationToken) =>
        await Write("UpsertAccount", new { id = Id(accountId), handle });

    public async Task Deactivate(Guid accountId, CancellationToken cancellationToken) =>
        await Write("Deactivate", new { id = Id(accountId) });

    public async Task<bool> Apply(EdgeChange change, CancellationToken cancellationToken)
    {
        var name = change.Kind switch
        {
            EdgeKind.Follows => "ApplyFollow",
            EdgeKind.Blocks => "ApplyBlock",
            _ => "ApplyMute",
        };
        var rows = await Write(name, new
        {
            from = Id(change.From),
            to = Id(change.To),
            v = change.Version,
            on = change.On,
            since = change.Since.ToUnixTimeMilliseconds(),
            step = change.On ? 1 : -1,
        });
        return rows.Count == 0
            ? throw new InvalidOperationException($"An account of {change.Kind} {change.From} → {change.To} is not in the graph yet; delivery retries.")
            : rows[0]["applied"].As<bool>();
    }

    public async Task<IReadOnlyList<Suggestion>> WhoToFollow(Guid reader, TraversalLimits limits, CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(CypherQueries.Get("WhoToFollow"), new
            {
                me = Id(reader),
                supernode = limits.SupernodeFollowers,
                maxFollowing = limits.MaxIntermediateFollowing,
                intermediates = limits.Intermediates,
                results = limits.Results,
            });
            return (IReadOnlyList<Suggestion>)[.. (await cursor.ToListAsync()).Select(r =>
                new Suggestion(Guid.Parse(r["id"].As<string>()), r["handle"].As<string>(), r["mutual"].As<long>()))];
        });
    }

    public async Task<IReadOnlyList<GraphDegrees>> Accounts(Guid? after, int limit, CancellationToken cancellationToken) =>
        [.. (await Read("AccountsPage", new { after = after is { } a ? Id(a) : "", limit })).Select(r =>
            new GraphDegrees(Guid.Parse(r["id"].As<string>()), r["followers"].As<long>(), r["following"].As<long>()))];

    public async Task RecountDegrees(Guid accountId, CancellationToken cancellationToken) =>
        await Write("RecountDegrees", new { id = Id(accountId) });

    public async Task<IReadOnlyList<Guid>> DuplicateAccounts(CancellationToken cancellationToken) =>
        [.. (await Read("DuplicateAccounts", new { })).Select(r => Guid.Parse(r["id"].As<string>()))];

    public async Task<long> CompactOffEdges(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        long deleted = 0;
        foreach (var query in new[] { "CompactFollows", "CompactBlocks", "CompactMutes" })
        {
            long batch;
            do
            {
                batch = (await Write(query, new { cutoff = cutoff.ToUnixTimeMilliseconds() }))[0]["deleted"].As<long>();
                deleted += batch;
            }
            while (batch == 10_000);
        }

        return deleted;
    }

    public async Task<IReadOnlyList<Suggestion>> FriendsOfFriends(Guid reader, TraversalLimits limits, CancellationToken cancellationToken) =>
        [.. (await Read("FriendsOfFriends", new
        {
            me = Id(reader), supernode = limits.SupernodeFollowers, maxFollowing = limits.MaxIntermediateFollowing,
            intermediates = limits.Intermediates, results = limits.Results,
        })).Select(r => new Suggestion(Guid.Parse(r["id"].As<string>()), r["handle"].As<string>(), r["mutual"].As<long>()))];

    public async Task<IReadOnlyList<Suggestion>> FollowersNotFollowedBack(Guid reader, int limit, TraversalLimits limits, CancellationToken cancellationToken) =>
        [.. (await Read("FollowersNotFollowedBack", new { me = Id(reader), limit }))
            .Select(r => new Suggestion(Guid.Parse(r["id"].As<string>()), r["handle"].As<string>(), 0))];

    public async Task<IReadOnlySet<Guid>> HiddenNow(Guid reader, IReadOnlyList<Guid> accounts, CancellationToken cancellationToken) =>
        (await Read("HiddenNow", new { me = Id(reader), ids = accounts.Select(Id).ToList() })).Select(r => Guid.Parse(r["id"].As<string>())).ToHashSet();

    public async Task<bool> Dismiss(Guid reader, Guid account, DateTimeOffset until, CancellationToken cancellationToken) =>
        (await Write("Dismiss", new { me = Id(reader), account = Id(account), until = until.ToUnixTimeMilliseconds() })).Count == 1;

    public async Task<long> CompactDismissals(DateTimeOffset now, CancellationToken cancellationToken)
    {
        long deleted = 0, batch;
        do
        {
            batch = (await Write("CompactDismissals", new { now = now.ToUnixTimeMilliseconds() }))[0]["deleted"].As<long>();
            deleted += batch;
        }
        while (batch == 10_000);

        return deleted;
    }

    private async Task<List<IRecord>> Read(string query, object parameters)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(CypherQueries.Get(query), parameters)).ToListAsync());
    }

    private async Task<List<IRecord>> Write(string query, object parameters)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx => await (await tx.RunAsync(CypherQueries.Get(query), parameters)).ToListAsync());
    }

    /// <summary>Graph ids are the UUID's lower-case string: both engines compare and index strings.</summary>
    private static string Id(Guid id) => id.ToString("D");
}

/// <summary>The Neo4j-only setup step: the uniqueness constraint on Account ids.</summary>
internal static class Neo4jSetup
{
    public static async Task Run(IDriver driver, CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx => await tx.RunAsync(CypherQueries.Read(CypherQueries.SetupResource).Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Trim().Length > 0).Aggregate((a, b) => a + "\n" + b)));
    }
}
