using Timeline.Domain;

namespace Timeline.UnitTests;

public class PageRateTests
{
    [Fact]
    public void A_slow_second_halves_the_rate_down_to_the_floor()
    {
        var rate = new PageRate(floor: 1, ceiling: 20);

        Assert.Equal(10, rate.Adjust(TimeSpan.FromMilliseconds(12)));
        Assert.Equal(5, rate.Adjust(TimeSpan.FromMilliseconds(6)));
        for (var i = 0; i < 10; i++)
        {
            rate.Adjust(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(1, rate.PagesPerSecond);
    }

    [Fact]
    public void A_fast_second_adds_ten_percent_up_to_the_ceiling()
    {
        var rate = new PageRate(floor: 1, ceiling: 20);
        rate.Adjust(TimeSpan.FromMilliseconds(10));

        Assert.Equal(11, rate.Adjust(TimeSpan.FromMilliseconds(1)), 3);
        for (var i = 0; i < 20; i++)
        {
            rate.Adjust(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(20, rate.PagesPerSecond);
    }

    [Fact]
    public void Exactly_five_milliseconds_is_not_slow_and_a_quiet_second_changes_nothing()
    {
        var rate = new PageRate(floor: 1, ceiling: 20);
        rate.Adjust(TimeSpan.FromMilliseconds(10));

        Assert.Equal(11, rate.Adjust(PageRate.Slow), 3);
        Assert.Equal(11, rate.Adjust(null), 3);
    }
}
