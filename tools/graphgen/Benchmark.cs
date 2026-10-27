using System.Diagnostics;
using System.Globalization;
using System.Text;

using Npgsql;

using Recommendations.Domain;

namespace GraphGen;

/// <summary>Times each query shape for the same sample of readers and reports p50 and p99 per shape.</summary>
internal static class Benchmark
{
    private const string Exclusions = """
          AND NOT EXISTS (SELECT 1 FROM graphgen.follow_edges x WHERE x.follower_id = @me AND x.followee_id = c.id AND x."on")
          AND NOT EXISTS (SELECT 1 FROM graphgen.hidden_edges b WHERE b.kind = 'block' AND b."on"
                          AND ((b.owner_id = @me AND b.target_id = c.id) OR (b.owner_id = c.id AND b.target_id = @me)))
          AND NOT EXISTS (SELECT 1 FROM graphgen.hidden_edges m WHERE m.kind = 'mute' AND m."on" AND m.owner_id = @me AND m.target_id = c.id)
        """;

    /// <summary>The shapes, by name. Each takes the same parameters, whether it uses them or not.</summary>
    private static readonly (string Name, string Sql)[] Shapes =
    [
        // 1. The plain two-hop join: every follow of every account the reader follows, no bound at all.
        ("two-hop, plain", $"""
            SELECT c.id, count(*) AS mutual
            FROM graphgen.follow_edges f
            JOIN graphgen.follow_edges s ON s.follower_id = f.followee_id AND s."on"
            JOIN graphgen.accounts c ON c.id = s.followee_id AND c.active
            WHERE f.follower_id = @me AND f."on" AND c.id <> @me
            {Exclusions}
            GROUP BY c.id ORDER BY mutual DESC, c.id LIMIT @results
            """),

        // 2. The PostgreSQL adapter's query (s2-l13): the first hop bounded (newest follows, no supernode, capped following).
        ("two-hop, capped (the s2-l13 adapter's)", $"""
            WITH first_hop AS (
                SELECT f.followee_id AS id FROM graphgen.follow_edges f
                JOIN graphgen.accounts i ON i.id = f.followee_id
                WHERE f.follower_id = @me AND f."on" AND i.followers < @supernode AND i.following <= @maxFollowing
                ORDER BY f.since DESC LIMIT @intermediates)
            SELECT c.id, count(*) AS mutual
            FROM first_hop h
            JOIN graphgen.follow_edges s ON s.follower_id = h.id AND s."on"
            JOIN graphgen.accounts c ON c.id = s.followee_id AND c.active
            WHERE c.id <> @me
            {Exclusions}
            GROUP BY c.id ORDER BY mutual DESC, c.id LIMIT @results
            """),

        // 3. Bounded per hop: a LATERAL subquery takes at most @perIntermediate follows of each intermediate.
        ("two-hop, bounded LATERAL", $"""
            WITH first_hop AS (
                SELECT f.followee_id AS id FROM graphgen.follow_edges f
                JOIN graphgen.accounts i ON i.id = f.followee_id
                WHERE f.follower_id = @me AND f."on" AND i.followers < @supernode AND i.following <= @maxFollowing
                ORDER BY f.since DESC LIMIT @intermediates)
            SELECT c.id, count(*) AS mutual
            FROM first_hop h
            CROSS JOIN LATERAL (
                SELECT s.followee_id FROM graphgen.follow_edges s
                WHERE s.follower_id = h.id AND s."on" ORDER BY s.since DESC LIMIT @perIntermediate) s
            JOIN graphgen.accounts c ON c.id = s.followee_id AND c.active
            WHERE c.id <> @me
            {Exclusions}
            GROUP BY c.id ORDER BY mutual DESC, c.id LIMIT @results
            """),

        // 4. Three hops with a recursive CTE: every path is built before any can be ranked or cut.
        ("three-hop, recursive CTE", $"""
            WITH RECURSIVE walk(id, depth) AS (
                SELECT f.followee_id, 1 FROM graphgen.follow_edges f WHERE f.follower_id = @me AND f."on"
                UNION ALL
                SELECT s.followee_id, w.depth + 1 FROM walk w
                JOIN graphgen.follow_edges s ON s.follower_id = w.id AND s."on"
                WHERE w.depth < 3)
            SELECT c.id, count(*) AS paths
            FROM walk w JOIN graphgen.accounts c ON c.id = w.id AND c.active
            WHERE w.depth >= 2 AND c.id <> @me
            {Exclusions}
            GROUP BY c.id ORDER BY paths DESC, c.id LIMIT @results
            """),
    ];

    public static async Task<string> Run(Options options, Graph graph)
    {
        var random = new Random(options.Seed + 1);
        var candidates = Enumerable.Range(0, graph.Accounts).Where(i => graph.Following[i] > 0).ToArray();
        var readers = Enumerable.Range(0, options.Readers).Select(_ => graph.Ids[candidates[random.Next(candidates.Length)]]).ToArray();
        var limits = TraversalLimits.Default;

        await using var db = new NpgsqlConnection(options.Connection);
        await db.OpenAsync();
        await using (var timeout = new NpgsqlCommand($"SET statement_timeout = {options.TimeoutMs}", db))
        {
            await timeout.ExecuteNonQueryAsync();
        }

        var version = (string)(await new NpgsqlCommand("SHOW server_version", db).ExecuteScalarAsync())!;
        var report = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        report.AppendLine("Local measurement (not an AWS figure).");
        report.AppendLine(c, $"Setup: {Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, PostgreSQL {version} in Docker, graphgen {options}.");
        report.AppendLine(c, $"Graph: {graph.Accounts:N0} accounts, {graph.Edges.Count:N0} follows (mean following {graph.MeanFollowing:F1}), max followers {graph.MaxFollowers:N0}, supernodes (>= 10,000 followers) {graph.Supernodes:N0}.");
        report.AppendLine(c, $"Limits: supernode {limits.SupernodeFollowers:N0} followers, intermediates following <= {limits.MaxIntermediateFollowing:N0}, first hop {limits.Intermediates} newest follows, 100 follows per intermediate (LATERAL only).");
        report.AppendLine("| Shape | p50 ms | p99 ms | timeouts |");
        report.AppendLine("|---|---|---|---|");
        foreach (var (name, sql) in Shapes)
        {
            var times = new List<double>();
            var timeouts = 0;
            foreach (var reader in readers)
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("me", reader);
                command.Parameters.AddWithValue("supernode", limits.SupernodeFollowers);
                command.Parameters.AddWithValue("maxFollowing", limits.MaxIntermediateFollowing);
                command.Parameters.AddWithValue("intermediates", limits.Intermediates);
                command.Parameters.AddWithValue("perIntermediate", 100);
                command.Parameters.AddWithValue("results", limits.Results);
                var clock = Stopwatch.StartNew();
                try
                {
                    await using var rows = await command.ExecuteReaderAsync();
                    while (await rows.ReadAsync())
                    {
                    }

                    times.Add(clock.Elapsed.TotalMilliseconds);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.QueryCanceled)
                {
                    timeouts++;
                    times.Add(options.TimeoutMs);
                }
            }

            times.Sort();
            report.AppendLine(c, $"| {name} | {Percentile(times, 0.50):F1} | {Percentile(times, 0.99):F1} | {timeouts} |");
        }

        report.AppendLine(c, $"A timed-out query is counted at the timeout ({options.TimeoutMs} ms), so a p99 equal to it means \"at least\".");
        return report.ToString();
    }

    private static double Percentile(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
}
