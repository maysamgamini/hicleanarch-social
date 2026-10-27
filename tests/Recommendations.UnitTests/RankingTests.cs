using Recommendations.Domain;

namespace Recommendations.UnitTests;

public class RankingTests
{
    [Fact]
    public void More_mutual_connections_first_then_account_id_in_PostgreSQLs_uuid_order()
    {
        var low = Guid.Parse("00000000-0000-7000-8000-000000000001");
        var high = Guid.Parse("ffffffff-0000-7000-8000-000000000001");
        var best = Guid.Parse("80000000-0000-7000-8000-000000000001");

        var ranked = SuggestionRanking.Rank([new(high, "h", 1), new(best, "b", 3), new(low, "l", 1)], 20);

        Assert.Equal([best, low, high], ranked.Select(s => s.AccountId));
    }

    [Fact]
    public void At_most_the_limit()
    {
        var many = Enumerable.Range(0, 50).Select(i => new Suggestion(Guid.NewGuid(), "x", i));

        Assert.Equal(20, SuggestionRanking.Rank(many, 20).Count);
    }

    [Fact]
    public void The_default_limits_are_the_courses()
    {
        Assert.Equal(new TraversalLimits(SupernodeFollowers: 10_000, MaxIntermediateFollowing: 1_000, Intermediates: 200, Results: 20), TraversalLimits.Default);
    }
}
