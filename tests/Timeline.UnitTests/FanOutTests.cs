using Timeline.Application;
using Timeline.Domain;

namespace Timeline.UnitTests;

public class FanOutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Audience(int size) : IFollowerPages
    {
        public List<int> PageSizes { get; } = [];

        public readonly List<Guid> All = [.. Enumerable.Range(0, size).Select(_ => Guid.NewGuid()).Order()];

        public Task<(IReadOnlyList<Guid> Followers, Guid? Next)> Page(Guid author, Guid? after, int limit, CancellationToken cancellationToken)
        {
            var start = after is null ? 0 : All.IndexOf(after.Value) + 1;
            var page = All.Skip(start).Take(limit).ToList();
            PageSizes.Add(page.Count);
            return Task.FromResult<(IReadOnlyList<Guid>, Guid?)>((page, start + page.Count < All.Count ? page[^1] : null));
        }
    }

    /// <summary>One job in memory, with the lease rule: a step by a token that no longer holds the job is refused.</summary>
    private sealed class OneJob(Guid post, Guid author) : IFanoutJobs
    {
        public FanoutJob? Stored { get; private set; } = new(post, author, FanoutPool.Large, null, false, 0, Guid.Empty);

        public bool Done { get; private set; }

        public Guid Holder { get; private set; }

        public Task Create(Guid postId, Guid authorId, long audience, FanoutPool pool, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FanoutJob?> Claim(FanoutPool pool, TimeSpan lease, CancellationToken cancellationToken)
        {
            if (Done || Holder != Guid.Empty)
            {
                return Task.FromResult<FanoutJob?>(null);
            }

            Holder = Guid.NewGuid();
            return Task.FromResult<FanoutJob?>(Stored! with { LeaseToken = Holder });
        }

        public Task<bool> Advance(FanoutJob job, TimeSpan lease, CancellationToken cancellationToken)
        {
            if (job.LeaseToken != Holder)
            {
                return Task.FromResult(false);
            }

            Stored = job;
            return Task.FromResult(true);
        }

        public Task<bool> Finish(FanoutJob job, CancellationToken cancellationToken)
        {
            Done = job.LeaseToken == Holder;
            return Task.FromResult(Done);
        }

        public Task Release(FanoutJob job, CancellationToken cancellationToken)
        {
            Holder = Guid.Empty;
            return Task.CompletedTask;
        }

        /// <summary>The lease ran out: the job is free again, from its last confirmed page.</summary>
        public void Expire() => Holder = Guid.Empty;
    }

    private sealed class Progress : IFanoutProgress
    {
        public bool Completed { get; private set; }

        public Task<Delivery> Start(Guid postId, Guid authorId, DateTimeOffset storedAt, CancellationToken cancellationToken) =>
            Task.FromResult(new Delivery(postId, authorId, storedAt, 0, null));

        public Task Reached(Guid postId, long followers, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> Complete(Guid postId, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.FromResult(true);
        }

        public Task<Delivery?> Find(Guid postId, CancellationToken cancellationToken) => Task.FromResult<Delivery?>(null);
    }

    /// <summary>Timelines that fail on a chosen call, as a worker that dies mid-job.</summary>
    private sealed class Dying(int failOnCall) : ITimelines
    {
        private readonly InMemoryTimelines _inner = new();
        private int _calls;

        public List<Guid> Written => _inner.Written;

        public Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken) =>
            ++_calls == failOnCall ? throw new IOException("the task died") : _inner.AddToExisting(owners, postId, cancellationToken);

        public Task<bool> Exists(Guid owner, CancellationToken cancellationToken) => _inner.Exists(owner, cancellationToken);

        public Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken) => _inner.CreateEmpty(owner, cancellationToken);

        public Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => _inner.Fill(owner, postIds, cancellationToken);

        public Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken) =>
            _inner.Read(owner, maxScore, skip, count, cancellationToken);

        public Task Touch(Guid owner, CancellationToken cancellationToken) => _inner.Touch(owner, cancellationToken);

        public Task Remove(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => _inner.Remove(owner, postIds, cancellationToken);
    }

    [Fact]
    public async Task A_job_writes_the_author_then_the_audience_a_thousand_at_a_time()
    {
        var (audience, timelines, author, post) = (new Audience(2_500), new InMemoryTimelines(), Guid.NewGuid(), Guid.CreateVersion7());
        var jobs = new OneJob(post, author);
        var progress = new Progress();

        await new FanoutRunner(jobs, timelines, audience, progress, TimeProvider.System).RunOne(FanoutPool.Large, TimeSpan.FromSeconds(30), null, Ct);

        Assert.Equal([1_000, 1_000, 500], audience.PageSizes);
        Assert.Equal(author, timelines.Written[0]);
        Assert.Equal(2_501, timelines.Written.Distinct().Count());
        Assert.True(jobs.Done);
        Assert.True(progress.Completed);
    }

    [Fact]
    public async Task A_worker_that_dies_mid_job_leaves_it_to_the_next_from_its_last_confirmed_page_with_no_page_skipped()
    {
        var (audience, author, post) = (new Audience(3_500), Guid.NewGuid(), Guid.CreateVersion7());
        var jobs = new OneJob(post, author);
        var dying = new Dying(failOnCall: 3); // author, page 1, then dies writing page 2

        await Assert.ThrowsAsync<IOException>(() => new FanoutRunner(jobs, dying, audience, new Progress(), TimeProvider.System)
            .RunOne(FanoutPool.Large, TimeSpan.FromSeconds(30), null, Ct));
        Assert.Equal(1_000, jobs.Stored!.Reached);

        jobs.Expire();
        var next = new InMemoryTimelines();
        await new FanoutRunner(jobs, next, audience, new Progress(), TimeProvider.System).RunOne(FanoutPool.Large, TimeSpan.FromSeconds(30), null, Ct);

        Assert.True(jobs.Done);
        Assert.Equal(audience.All.Skip(1_000), next.Written);
        Assert.Equal(audience.All.Concat([author]).Order(), dying.Written.Concat(next.Written).Order());
    }

    [Fact]
    public async Task A_job_held_by_one_worker_is_not_given_to_another()
    {
        var jobs = new OneJob(Guid.CreateVersion7(), Guid.NewGuid());

        var first = await jobs.Claim(FanoutPool.Large, TimeSpan.FromSeconds(30), Ct);
        var second = await jobs.Claim(FanoutPool.Large, TimeSpan.FromSeconds(30), Ct);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Theory]
    [InlineData(0, FanoutPool.Small)]
    [InlineData(999, FanoutPool.Small)]
    [InlineData(1_000, FanoutPool.Large)]
    [InlineData(25_000_000, FanoutPool.Large)]
    public void The_pool_is_chosen_by_audience_size(long audience, FanoutPool pool) =>
        Assert.Equal(pool, FanoutPools.For(audience));

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
