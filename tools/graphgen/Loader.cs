using Microsoft.EntityFrameworkCore;

using Npgsql;

using NpgsqlTypes;

using Recommendations.Infrastructure.Persistence;

namespace GraphGen;

/// <summary>Migrates Recommendations' schema and loads the graph with COPY, replacing what was there.</summary>
internal static class Loader
{
    public static async Task Load(string connection, Graph graph)
    {
        await new RecommendationsMigrator().Migrate(connection, CancellationToken.None);
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using (var clear = new NpgsqlCommand("TRUNCATE recommendations.hidden_edges, recommendations.follow_edges, recommendations.accounts", db))
        {
            await clear.ExecuteNonQueryAsync();
        }

        await using (var accounts = await db.BeginBinaryImportAsync("COPY recommendations.accounts (id, handle, active, followers, following) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < graph.Accounts; i++)
            {
                await accounts.StartRowAsync();
                await accounts.WriteAsync(graph.Ids[i], NpgsqlDbType.Uuid);
                await accounts.WriteAsync("g" + i, NpgsqlDbType.Varchar);
                await accounts.WriteAsync(true, NpgsqlDbType.Boolean);
                await accounts.WriteAsync((long)graph.Followers[i], NpgsqlDbType.Bigint);
                await accounts.WriteAsync((long)graph.Following[i], NpgsqlDbType.Bigint);
            }

            await accounts.CompleteAsync();
        }

        var start = DateTimeOffset.UtcNow.AddDays(-365);
        await using (var edges = await db.BeginBinaryImportAsync("COPY recommendations.follow_edges (follower_id, followee_id, v, \"on\", since) FROM STDIN (FORMAT BINARY)"))
        {
            for (var e = 0; e < graph.Edges.Count; e++)
            {
                var (from, to) = graph.Edges[e];
                await edges.StartRowAsync();
                await edges.WriteAsync(graph.Ids[from], NpgsqlDbType.Uuid);
                await edges.WriteAsync(graph.Ids[to], NpgsqlDbType.Uuid);
                await edges.WriteAsync(1L, NpgsqlDbType.Bigint);
                await edges.WriteAsync(true, NpgsqlDbType.Boolean);
                await edges.WriteAsync(start.AddSeconds(e), NpgsqlDbType.TimestampTz);
            }

            await edges.CompleteAsync();
        }

        await using var analyze = new NpgsqlCommand("ANALYZE recommendations.accounts; ANALYZE recommendations.follow_edges;", db);
        await analyze.ExecuteNonQueryAsync();
    }
}
