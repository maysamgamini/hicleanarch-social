using Timeline.Domain;

namespace Timeline.UnitTests;

public class PullRulesTests
{
    private static readonly Guid Author = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_author_is_promoted_at_25000_followers_and_not_before()
    {
        Assert.Null(PulledAuthor.Next(null, Author, 24_999, Now));
        Assert.Equal(new PulledAuthor(Author, Now), PulledAuthor.Next(null, Author, 25_000, Now));
    }

    [Fact]
    public void Between_20000_and_25000_nothing_changes_either_way()
    {
        var pulled = new PulledAuthor(Author, Now.AddDays(-1));
        Assert.Same(pulled, PulledAuthor.Next(pulled, Author, 20_000, Now));
        Assert.Null(PulledAuthor.Next(null, Author, 24_000, Now));
    }

    [Fact]
    public void Below_20000_a_pulled_author_is_demoted_once_and_promoted_again_at_25000()
    {
        var pulled = new PulledAuthor(Author, Now.AddDays(-1));
        var demoted = PulledAuthor.Next(pulled, Author, 19_999, Now)!;
        Assert.Equal(Now, demoted.DemotedAt);
        Assert.Same(demoted, PulledAuthor.Next(demoted, Author, 10, Now.AddHours(1)));
        Assert.Equal(new PulledAuthor(Author, Now.AddHours(2)), PulledAuthor.Next(demoted, Author, 25_000, Now.AddHours(2)));
    }

    [Fact]
    public void A_promoted_author_is_still_pushed_for_twelve_minutes_and_pulled_from_the_start()
    {
        var promoted = new PulledAuthor(Author, Now);
        Assert.Equal(TimeSpan.FromMinutes(12), PullRules.PushAfterPromotion);
        Assert.True(promoted.Pushed(Now.AddMinutes(11)));
        Assert.False(promoted.Pushed(Now.AddMinutes(12)));
        Assert.True(promoted.Pulled(Now));
    }

    [Fact]
    public void A_demoted_author_is_pushed_at_once_and_still_pulled_for_three_days()
    {
        var demoted = new PulledAuthor(Author, Now.AddDays(-10), Now);
        Assert.True(demoted.Pushed(Now));
        Assert.True(demoted.Pulled(Now.AddDays(3).AddMinutes(-1)));
        Assert.False(demoted.Pulled(Now.AddDays(3)));
    }
}
