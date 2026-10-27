using Relationships.Domain;

namespace Relationships.UnitTests;

public class PairRulesTests
{
    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    [Fact]
    public void Following_someone_not_yet_followed_adds_a_follow() =>
        Assert.Equal(Change.Added, new Pair(1, []).Follow(Ada, Bob).Value);

    [Fact]
    public void Following_again_changes_nothing() =>
        Assert.Equal(Change.Unchanged, new Pair(2, [new Edge(EdgeKind.Follow, Ada, Bob)]).Follow(Ada, Bob).Value);

    [Fact]
    public void A_follow_has_a_direction()
    {
        var pair = new Pair(2, [new Edge(EdgeKind.Follow, Bob, Ada)]);

        Assert.Equal(Change.Added, pair.Follow(Ada, Bob).Value);
        Assert.Equal(Change.Unchanged, pair.Unfollow(Ada, Bob));
    }

    [Fact]
    public void Unfollowing_removes_only_an_existing_follow()
    {
        Assert.Equal(Change.Removed, new Pair(2, [new Edge(EdgeKind.Follow, Ada, Bob)]).Unfollow(Ada, Bob));
        Assert.Equal(Change.Unchanged, new Pair(2, []).Unfollow(Ada, Bob));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_follow_across_a_block_is_refused_whichever_side_blocked(bool adaBlocked)
    {
        var pair = new Pair(3, [adaBlocked ? new Edge(EdgeKind.Block, Ada, Bob) : new Edge(EdgeKind.Block, Bob, Ada)]);

        Assert.Equal("blocked", Assert.Single(pair.Follow(Ada, Bob).Errors).Code);
        Assert.Equal("blocked", Assert.Single(pair.Follow(Bob, Ada).Errors).Code);
    }

    [Fact]
    public void A_block_removes_the_follows_in_both_directions()
    {
        var pair = new Pair(4, [new Edge(EdgeKind.Follow, Ada, Bob), new Edge(EdgeKind.Follow, Bob, Ada)]);

        var outcome = pair.Block(Ada, Bob);

        Assert.Equal(Change.Added, outcome.Block);
        Assert.Equal([new Edge(EdgeKind.Follow, Ada, Bob), new Edge(EdgeKind.Follow, Bob, Ada)], outcome.RemovedFollows);
    }

    [Fact]
    public void A_mute_is_one_way_and_leaves_follows_and_follows_allowed()
    {
        var pair = new Pair(5, [new Edge(EdgeKind.Mute, Ada, Bob), new Edge(EdgeKind.Follow, Ada, Bob)]);

        Assert.Equal(Change.Unchanged, pair.Mute(Ada, Bob));
        Assert.Equal(Change.Added, pair.Mute(Bob, Ada));
        Assert.Equal(Change.Added, pair.Follow(Bob, Ada).Value);
        Assert.Equal(Change.Unchanged, pair.Follow(Ada, Bob).Value);
    }
}
