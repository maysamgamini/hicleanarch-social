using System.Diagnostics;
using System.Globalization;
using System.Text;

using Neo4j.Driver;

using Recommendations.Domain;
using Recommendations.Infrastructure.Graph;

namespace GraphGen;

/// <summary>The same graph and the same readers, on the graph database, with the adapter's own query.</summary>
internal static class Neo4jBenchmark
{
    public static async Task<string> Run(string uri, Options options, Graph graph)
    {
        await using var driver = GraphDatabase.Driver(uri, AuthTokens.None);
        await Neo4jSetup.Run(driver, CancellationToken.None);
        await Load(driver, graph);

        var random = new Random(options.Seed + 1); // the same readers as the SQL shapes
        var candidates = Enumerable.Range(0, graph.Accounts).Where(i => graph.Following[i] > 0).ToArray();
        var readers = Enumerable.Range(0, options.Readers).Select(_ => graph.Ids[candidates[random.Next(candidates.Length)]]).ToArray();
        var adapter = new Neo4jGraph(driver);
        var times = new List<double>();
        foreach (var reader in readers)
        {
            var clock = Stopwatch.StartNew();
            await adapter.WhoToFollow(reader, TraversalLimits.Default, CancellationToken.None);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var c = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine(c, $"Neo4j 5.26 Community in Docker, the same graph and readers ({options.Readers}), the adapter's WhoToFollow query:");
        report.AppendLine("| Shape | p50 ms | p99 ms | timeouts |");
        report.AppendLine("|---|---|---|---|");
        report.AppendLine(c, $"| two-hop, capped (Neo4j adapter) | {Percentile(times, 0.50):F1} | {Percentile(times, 0.99):F1} | 0 |");
        return report.ToString();
    }

    private static async Task Load(IDriver driver, Graph graph)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(tx => tx.RunAsync("MATCH (a:Account) WHERE a.handle STARTS WITH 'g' DETACH DELETE a"));
        foreach (var chunk in Enumerable.Range(0, graph.Accounts).Chunk(10_000))
        {
            var rows = chunk.Select(i => new Dictionary<string, object>
            {
                ["id"] = graph.Ids[i].ToString("D"), ["handle"] = "g" + i, ["followers"] = (long)graph.Followers[i], ["following"] = (long)graph.Following[i],
            }).ToList();
            await session.ExecuteWriteAsync(tx => tx.RunAsync(
                "UNWIND $rows AS row CREATE (:Account {id: row.id, handle: row.handle, active: true, followers: row.followers, following: row.following})",
                new { rows }));
        }

        var since = DateTimeOffset.UtcNow.AddDays(-365).ToUnixTimeMilliseconds();
        foreach (var chunk in Enumerable.Range(0, graph.Edges.Count).Chunk(10_000))
        {
            var rows = chunk.Select(e => new Dictionary<string, object>
            {
                ["a"] = graph.Ids[graph.Edges[e].From].ToString("D"), ["b"] = graph.Ids[graph.Edges[e].To].ToString("D"), ["since"] = since + (e * 1000L),
            }).ToList();
            await session.ExecuteWriteAsync(tx => tx.RunAsync(
                "UNWIND $rows AS row MATCH (a:Account {id: row.a}), (b:Account {id: row.b}) CREATE (a)-[:FOLLOWS {v: 1, on: true, since: row.since}]->(b)",
                new { rows }));
        }
    }

    private static double Percentile(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
}
