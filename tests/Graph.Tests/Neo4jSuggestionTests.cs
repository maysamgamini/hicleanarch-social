using Neo4j.Driver;

using Recommendations.Application;
using Recommendations.Domain;
using Recommendations.Infrastructure.Graph;

namespace Graph.Tests;

/// <summary>The same suggestion rules, unchanged, on the graph database.</summary>
public sealed class Neo4jSuggestionTests(Neo4jServer neo4j) : SuggestionContract
{
    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    protected override Task<(IGraphProjection Projection, IGraphReads Reads, IAsyncDisposable Store)> Connect()
    {
        var graph = new Neo4jGraph(neo4j.Driver);
        return Task.FromResult<(IGraphProjection, IGraphReads, IAsyncDisposable)>((graph, graph, new Nothing()));
    }

    protected override async Task MakeSupernode(Guid account)
    {
        await using var session = neo4j.Driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync("MATCH (a:Account {id: $id}) SET a.followers = $n", new { id = account.ToString("D"), n = Limits.SupernodeFollowers }));
    }

    [Fact]
    public async Task A_supernode_with_50000_real_followers_never_appears_as_an_intermediate()
    {
        var (me, star, viaStar) = (await Account(), await Account(), await Account());
        await Follow(me, star);
        await Follow(star, viaStar);
        // 50,000 followers, projected as edges in batches, so the degree is the projection's own count.
        await using (var session = neo4j.Driver.AsyncSession())
        {
            for (var batch = 0; batch < 50; batch++)
            {
                var fans = Enumerable.Range(0, 1_000).Select(_ => Guid.CreateVersion7().ToString("D")).ToList();
                await session.ExecuteWriteAsync(tx => tx.RunAsync("""
                    MATCH (s:Account {id: $star})
                    UNWIND $fans AS fan
                    CREATE (f:Account {id: fan, handle: 'fan', active: true, followers: 0, following: 1})
                    CREATE (f)-[:FOLLOWS {v: 1, on: true, since: 0}]->(s)
                    WITH s, count(*) AS n
                    SET s.followers = s.followers + n
                    """, new { star = star.ToString("D"), fans }));
            }
        }

        var suggested = await Suggest(me);

        Assert.DoesNotContain(viaStar, suggested.Select(s => s.AccountId));
    }

    [Fact]
    public async Task Two_concurrent_projections_of_one_pair_end_at_the_higher_version()
    {
        for (var run = 0; run < 30; run++)
        {
            var (a, b) = (await Account(), await Account());
            var started = new EdgeChange(EdgeKind.Follows, a, b, 5, true, DateTimeOffset.UtcNow);
            var ended = new EdgeChange(EdgeKind.Follows, a, b, 6, false, DateTimeOffset.UtcNow);

            await Task.WhenAll(Task.Run(() => Projection.Apply(started, Ct)), Task.Run(() => Projection.Apply(ended, Ct)));

            await using var session = neo4j.Driver.AsyncSession();
            var edges = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
                "MATCH (:Account {id: $a})-[r:FOLLOWS]->(:Account {id: $b}) RETURN r.v AS v, r.on AS on",
                new { a = a.ToString("D"), b = b.ToString("D") })).ToListAsync());
            var degree = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
                "MATCH (x:Account {id: $a}) RETURN x.following AS n", new { a = a.ToString("D") })).SingleAsync());

            var edge = Assert.Single(edges);
            Assert.Equal(6L, edge["v"].As<long>());
            Assert.False(edge["on"].As<bool>());
            Assert.Equal(0L, degree["n"].As<long>());
        }
    }

    private async Task<long> Followers(Guid account)
    {
        await using var session = neo4j.Driver.AsyncSession();
        var record = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
            "MATCH (a:Account {id: $id}) RETURN a.followers AS n", new { id = account.ToString("D") })).SingleAsync());
        return record["n"].As<long>();
    }

    [Fact]
    public async Task A_follow_does_not_write_the_followees_node_and_the_announced_count_sets_its_degree()
    {
        var (fan, star) = (await Account(), await Account());
        var graph = (Neo4jGraph)Projection;

        await Follow(fan, star);
        Assert.Equal(0L, await Followers(star)); // the edge's transaction left the followee alone

        var now = DateTimeOffset.UtcNow;
        Assert.True(await graph.SetFollowers(star, 25_000, now, Ct));
        Assert.False(await graph.SetFollowers(star, 24_000, now.AddSeconds(-1), Ct)); // an older announcement, delivered late
        Assert.Equal(25_000L, await Followers(star));
    }
}
