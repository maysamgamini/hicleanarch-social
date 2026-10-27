using Timeline.Application;
using Timeline.Domain;

namespace Timeline.UnitTests;

public class FanOutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Audience(int size) : IFollowerPages
    {
        public List<int> PageSizes { get; } = [];

        private readonly List<Guid> _followers = [.. Enumerable.Range(0, size).Select(_ => Guid.NewGuid()).Order()];

        public Task<(IReadOnlyList<Guid> Followers, Guid? Next)> Page(Guid author, Guid? after, int limit, CancellationToken cancellationToken)
        {
            var start = after is null ? 0 : _followers.IndexOf(after.Value) + 1;
            var page = _followers.Skip(start).Take(limit).ToList();
            PageSizes.Add(page.Count);
            return Task.FromResult<(IReadOnlyList<Guid>, Guid?)>((page, start + page.Count < _followers.Count ? page[^1] : null));
        }
    }

    private sealed class Progress : IFanoutProgress
    {
        private readonly Dictionary<Guid, Delivery> _rows = [];

        public Task<Delivery> Start(Guid postId, Guid authorId, DateTimeOffset storedAt, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(postId, out var row) ? row : _rows[postId] = new Delivery(postId, authorId, storedAt, 0, null));

        public Task Reached(Guid postId, long followers, CancellationToken cancellationToken)
        {
            _rows[postId] = _rows[postId] with { FollowersReached = followers };
            return Task.CompletedTask;
        }

        public Task<bool> Complete(Guid postId, DateTimeOffset at, CancellationToken cancellationToken)
        {
            var open = _rows[postId].FannedOutAt is null;
            _rows[postId] = _rows[postId] with { FannedOutAt = _rows[postId].FannedOutAt ?? at };
            return Task.FromResult(open);
        }

        public Task<Delivery?> Find(Guid postId, CancellationToken cancellationToken) => Task.FromResult(_rows.GetValueOrDefault(postId));
    }

    [Fact]
    public async Task The_audience_is_read_a_thousand_at_a_time_and_the_author_is_included()
    {
        var (audience, timelines, author) = (new Audience(2_500), new InMemoryTimelines(), Guid.NewGuid());

        var result = await new FanOutHandler(timelines, audience, new Progress(), TimeProvider.System).Handle(new FanOut(Guid.CreateVersion7(), author, DateTimeOffset.UtcNow), Ct);

        Assert.Equal([1_000, 1_000, 500], audience.PageSizes);
        Assert.Equal(2_501, timelines.Written.Distinct().Count());
        Assert.Contains(author, timelines.Written);
        Assert.Equal(2_500, result.Value.FollowersPaged);
    }

    [Fact]
    public void A_score_is_the_post_ids_unix_milliseconds()
    {
        var written = DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_123);

        Assert.Equal(1_793_000_000_123, TimelineRules.Score(Guid.CreateVersion7(written)));
    }

    [Fact]
    public void A_score_fits_a_double_exactly()
    {
        var score = TimelineRules.Score(Guid.CreateVersion7(DateTimeOffset.MaxValue.AddDays(-1)));

        Assert.Equal(score, (long)(double)score);
    }

    [Fact]
    public void Only_a_UUIDv7_has_a_score() =>
        Assert.Throws<ArgumentException>(() => TimelineRules.Score(Guid.NewGuid()));
}
