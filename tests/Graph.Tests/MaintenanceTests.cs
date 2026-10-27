using Neo4j.Driver;

using Recommendations.Application;
using Recommendations.Domain;
using Recommendations.Infrastructure.Graph;

namespace Graph.Tests;

/// <summary>The jobs walk the whole shared graph, so they run alone, after every other class.</summary>
[CollectionDefinition(nameof(WholeGraph), DisableParallelization = true)]
public sealed class WholeGraph;

/// <summary>The graph's nightly jobs on Neo4j: the reconciliation repairs, the compaction forgets only old off edges.</summary>
[Collection(nameof(WholeGraph))]
public sealed class MaintenanceTests(Neo4jServer neo4j)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Neo4jGraph _graph = new(neo4j.Driver);

    /// <summary>The source of truth, as Relationships would answer: everyone's counts and pairs from a list of follows.</summary>
    private sealed class Truth(List<(Guid From, Guid To)> follows) : IRelationshipSource
    {
        public Task<IReadOnlyDictionary<Guid, (long Followers, long Following)>> Counts(IReadOnlyList<Guid> accounts, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (long, long)>>(accounts.ToDictionary(a => a,
                a => ((long)follows.Count(f => f.To == a), (long)follows.Count(f => f.From == a))));

        public Task<IReadOnlyList<EdgeChange>> PairsOf(Guid account, DateTimeOffset at, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EdgeChange>>([.. follows.Where(f => f.From == account || f.To == account)
                .Select(f => new EdgeChange(EdgeKind.Follows, f.From, f.To, 1, true, at))]);
    }

    private async Task<Guid> Account()
    {
        var id = Guid.CreateVersion7();
        await _graph.UpsertAccount(id, "m" + id.ToString("N")[^8..], Ct);
        return id;
    }

    private async Task<long> Property(Guid account, string name)
    {
        await using var session = neo4j.Driver.AsyncSession();
        var record = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
            $"MATCH (a:Account {{id: $id}}) RETURN a.{name} AS n", new { id = account.ToString("D") })).SingleAsync());
        return record["n"].As<long>();
    }

    [Fact]
    public async Task A_degree_changed_by_hand_in_the_graph_is_repaired_by_the_reconciliation()
    {
        var (a, b) = (await Account(), await Account());
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, b, 1, true, DateTimeOffset.UtcNow), Ct);
        await using (var session = neo4j.Driver.AsyncSession())
        {
            await session.ExecuteWriteAsync(tx => tx.RunAsync("MATCH (x:Account {id: $id}) SET x.followers = 41", new { id = b.ToString("D") }));
        }

        var handlers = new MaintenanceHandlers(_graph, _graph, new Truth([(a, b)]), TimeProvider.System);
        var report = (await handlers.Handle(new Reconcile(), Ct)).Value;

        Assert.Equal(1L, await Property(b, "followers"));
        Assert.True(report.Repaired >= 1);
        Assert.Empty(report.Duplicates); // Neo4j's constraint makes duplicates impossible here
    }

    [Fact]
    public async Task A_missing_edge_is_projected_again_by_the_reconciliation()
    {
        var (a, b) = (await Account(), await Account());

        var handlers = new MaintenanceHandlers(_graph, _graph, new Truth([(a, b)]), TimeProvider.System);
        await handlers.Handle(new Reconcile(), Ct);

        Assert.Equal(1L, await Property(a, "following"));
        Assert.Equal(1L, await Property(b, "followers"));
    }

    [Fact]
    public async Task Compaction_deletes_edges_off_for_more_than_30_days_and_keeps_younger_ones()
    {
        var (a, old, young, on) = (await Account(), await Account(), await Account(), await Account());
        var now = DateTimeOffset.UtcNow;
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, old, 2, false, now.AddDays(-31)), Ct);
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, young, 2, false, now.AddDays(-10)), Ct);
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, on, 2, true, now.AddDays(-400)), Ct);

        var handlers = new MaintenanceHandlers(_graph, _graph, new Truth([]), TimeProvider.System);
        await handlers.Handle(new Compact(), Ct);

        await using var session = neo4j.Driver.AsyncSession();
        var left = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
            "MATCH (:Account {id: $a})-[r:FOLLOWS]->(b:Account) RETURN b.id AS id", new { a = a.ToString("D") })).ToListAsync());
        Assert.Equal(new[] { young, on }.Select(i => i.ToString("D")).Order(), left.Select(r => r["id"].As<string>()).Order());
    }
}
