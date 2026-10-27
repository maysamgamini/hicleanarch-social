using Relationships.Domain;

namespace Relationships.UnitTests;

public class PairRulesTests
{
    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    [Fact]
    public void Following_someone_not_yet_followed_adds_a_follow() =>
        Assert.Equal(Change.Added, new Pair(1, []).Follow(Ada, Bob));

    [Fact]
    public void Following_again_changes_nothing() =>
        Assert.Equal(Change.Unchanged, new Pair(2, [new Edge(EdgeKind.Follow, Ada, Bob)]).Follow(Ada, Bob));

    [Fact]
    public void A_follow_has_a_direction()
    {
        var pair = new Pair(2, [new Edge(EdgeKind.Follow, Bob, Ada)]);

        Assert.Equal(Change.Added, pair.Follow(Ada, Bob));
        Assert.Equal(Change.Unchanged, pair.Unfollow(Ada, Bob));
    }

    [Fact]
    public void Unfollowing_removes_only_an_existing_follow()
    {
        Assert.Equal(Change.Removed, new Pair(2, [new Edge(EdgeKind.Follow, Ada, Bob)]).Unfollow(Ada, Bob));
        Assert.Equal(Change.Unchanged, new Pair(2, []).Unfollow(Ada, Bob));
    }
}
