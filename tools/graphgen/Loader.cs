using Npgsql;

using NpgsqlTypes;

namespace GraphGen;

/// <summary>
/// Loads the graph with COPY into a schema of the benchmark's own, shaped as the PostgreSQL adapter's
/// tables were (s2-l13), replacing what was there.
/// </summary>
internal static class Loader
{
    public static async Task Load(string connection, Graph graph)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using (var schema = new NpgsqlCommand(Schema, db))
        {
            await schema.ExecuteNonQueryAsync();
        }

        await using (var accounts = await db.BeginBinaryImportAsync("COPY graphgen.accounts (id, handle, active, followers, following) FROM STDIN (FORMAT BINARY)"))
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
        await using (var edges = await db.BeginBinaryImportAsync("COPY graphgen.follow_edges (follower_id, followee_id, v, \"on\", since) FROM STDIN (FORMAT BINARY)"))
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

        await using var index = new NpgsqlCommand(Indexes, db) { CommandTimeout = 0 };
        await index.ExecuteNonQueryAsync();
    }

    private const string Schema = """
        DROP SCHEMA IF EXISTS graphgen CASCADE;
        CREATE SCHEMA graphgen;
        CREATE TABLE graphgen.accounts (id uuid PRIMARY KEY, handle varchar(30) NOT NULL, active boolean NOT NULL, followers bigint NOT NULL, following bigint NOT NULL);
        CREATE TABLE graphgen.follow_edges (follower_id uuid NOT NULL, followee_id uuid NOT NULL, v bigint NOT NULL, "on" boolean NOT NULL, since timestamptz NOT NULL);
        CREATE TABLE graphgen.hidden_edges (owner_id uuid NOT NULL, target_id uuid NOT NULL, kind varchar(10) NOT NULL, v bigint NOT NULL, "on" boolean NOT NULL, since timestamptz NOT NULL,
            PRIMARY KEY (owner_id, target_id, kind));
        """;

    // The indexes the adapter had, built after the load (faster than maintaining them during COPY).
    private const string Indexes = """
        ALTER TABLE graphgen.follow_edges ADD PRIMARY KEY (follower_id, followee_id);
        CREATE INDEX follow_edges_newest ON graphgen.follow_edges (follower_id, since DESC) WHERE "on";
        ANALYZE graphgen.accounts; ANALYZE graphgen.follow_edges;
        """;
}
