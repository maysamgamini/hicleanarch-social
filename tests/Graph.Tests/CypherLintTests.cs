using Recommendations.Infrastructure.Graph;

namespace Graph.Tests;

public class CypherLintTests
{
    public static TheoryData<string> Queries() => [.. CypherQueries.Named.Keys];

    [Theory]
    [MemberData(nameof(Queries))]
    public void Every_query_the_adapter_runs_is_inside_the_subset(string name) =>
        Assert.Empty(CypherLint.Violations(CypherQueries.Get(name)));

    [Fact]
    public void The_queries_file_holds_every_query_the_adapter_names() =>
        Assert.Superset(new HashSet<string> { "UpsertAccount", "Deactivate", "ApplyFollow", "ApplyBlock", "ApplyMute", "WhoToFollow" },
            CypherQueries.Named.Keys.ToHashSet());

    [Theory]
    [InlineData("CALL apoc.path.expand(a, 'FOLLOWS>', null, 1, 2) YIELD path RETURN path", "CALL or YIELD (procedures)")]
    [InlineData("CALL { MATCH (a) RETURN a } RETURN a", "CALL or YIELD (procedures)")]
    [InlineData("MATCH p = shortestPath((a)-[*]-(b)) RETURN p", "shortestPath")]
    [InlineData("MATCH p = allShortestPaths((a)-[*]-(b)) RETURN p", "shortestPath")]
    [InlineData("MATCH (a) WHERE COUNT { (a)-->() } > 3 RETURN a", "COUNT, EXISTS or COLLECT subquery")]
    [InlineData("MATCH (a) WHERE EXISTS { (a)-[:FOLLOWS]->(:Account) } RETURN a", "COUNT, EXISTS or COLLECT subquery")]
    [InlineData("MATCH (a) RETURN [(a)-[:FOLLOWS]->(b) | b.id] AS ids", "pattern comprehension")]
    [InlineData("MATCH (a) WITH a, a.followers AS n RETURN a LIMIT n", "SKIP or LIMIT not a constant or a parameter")]
    [InlineData("MATCH (a) RETURN a SKIP toInteger(rand() * 10)", "SKIP or LIMIT not a constant or a parameter")]
    [InlineData("MATCH (a) RETURN elementId(a)", "elementId or ~id")]
    [InlineData("CREATE CONSTRAINT x IF NOT EXISTS FOR (a:Account) REQUIRE a.id IS UNIQUE", "CREATE INDEX or CREATE CONSTRAINT")]
    [InlineData("MATCH (a) RETURN 2 ^ 3", "^ (exponent)")]
    [InlineData("MATCH (a) WHERE a.at < datetime() - duration('P90D') RETURN a", "datetime arithmetic")]
    [InlineData("MATCH (a) RETURN size((a)-->()) AS degree", "size() over a pattern")]
    [InlineData("MATCH (a) SET a.x = 1 RETURN a UNION MATCH (b) RETURN b", "UNION in a query that writes")]
    public void The_lint_refuses_what_Neptune_does_not_run(string query, string rule) =>
        Assert.Contains(rule, CypherLint.Violations(query));

    [Theory]
    [InlineData("MATCH (a:Account {id: $id}) RETURN a LIMIT $n")]
    [InlineData("MATCH (a) RETURN a SKIP 10 LIMIT 20")]
    [InlineData("MATCH (a)-[:FOLLOWS]->(b) WHERE NOT (b)-[:BLOCKS]->(a) RETURN b.id, count(*) ORDER BY b.id")]
    [InlineData("MATCH (a) RETURN size(a.handle) AS length, coalesce(a.x, 0), toString(a.id), CASE WHEN a.on THEN 1 ELSE 0 END")]
    [InlineData("MATCH (a) RETURN a.id UNION MATCH (b) RETURN b.id")]
    [InlineData("MATCH (a) WHERE a.handle = 'call me' RETURN a")]
    public void The_lint_accepts_what_both_engines_run(string query) =>
        Assert.Empty(CypherLint.Violations(query));
}
