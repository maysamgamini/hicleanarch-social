using Neo4j.Driver;

using Recommendations.Infrastructure.Graph;

namespace Graph.Tests;

/// <summary>
/// Opt-in: runs every query of Queries.cypher against a Neptune endpoint, each inside a transaction
/// that is rolled back, when NEPTUNE_ENDPOINT is set (bolt+s://…:8182, from inside the VPC). Without
/// it the test is skipped. Running it is a learner exercise in the learner's own account.
/// </summary>
public class NeptuneQueriesTests
{
    private static readonly Dictionary<string, object> Parameters = new()
    {
        ["id"] = Guid.CreateVersion7().ToString("D"),
        ["handle"] = "neptune_check",
        ["from"] = Guid.CreateVersion7().ToString("D"),
        ["to"] = Guid.CreateVersion7().ToString("D"),
        ["v"] = 1L,
        ["on"] = true,
        ["since"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["step"] = 1,
        ["me"] = Guid.CreateVersion7().ToString("D"),
        ["supernode"] = 10_000,
        ["maxFollowing"] = 1_000,
        ["intermediates"] = 200,
        ["results"] = 20,
        ["after"] = "",
        ["limit"] = 10,
        ["cutoff"] = 0L,
    };

    public static TheoryData<string> Queries() => [.. CypherQueries.Named.Keys];

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_query_runs_on_Neptune(string name)
    {
        var endpoint = Environment.GetEnvironmentVariable("NEPTUNE_ENDPOINT");
        Assert.SkipWhen(string.IsNullOrEmpty(endpoint), "NEPTUNE_ENDPOINT is not set: the Neptune run is a learner exercise.");

        await using var driver = GraphDatabase.Driver(endpoint, AuthTokens.None, o => o.WithEncryptionLevel(EncryptionLevel.Encrypted));
        await using var session = driver.AsyncSession();
        var transaction = await session.BeginTransactionAsync();
        try
        {
            await (await transaction.RunAsync(CypherQueries.Get(name), Parameters)).ConsumeAsync();
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
