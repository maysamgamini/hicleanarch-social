using Timeline.Application;

namespace Timeline.UnitTests;

public class ReadFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Reader = Guid.NewGuid();
    private static readonly Guid Author = Guid.NewGuid();

    /// <summary>Posts and profiles in memory; a post can be deleted, and every call is counted.</summary>
    private sealed class Store : IPostSummaries, IProfiles, IFollowing
    {
        public List<PostCard> Posts { get; } = [];

        public HashSet<Guid> Deleted { get; } = [];

        public int PostCalls { get; private set; }

        public int ProfileCalls { get; private set; }

        public Task<IReadOnlyList<PostCard>> Get(IReadOnlyList<Guid> postIds, CancellationToken cancellationToken)
        {
            PostCalls++;
            return Task.FromResult<IReadOnlyList<PostCard>>([.. Posts.Where(p => postIds.Contains(p.PostId) && !Deleted.Contains(p.PostId))]);
        }

        public Task<IReadOnlyList<PostCard>> RecentByAuthors(IReadOnlyList<Guid> authors, int perAuthor, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PostCard>>([.. Posts.Where(p => authors.Contains(p.AuthorId) && !Deleted.Contains(p.PostId))]);

        Task<IReadOnlyList<AuthorCard>> IProfiles.Get(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken)
        {
            ProfileCalls++;
            return Task.FromResult<IReadOnlyList<AuthorCard>>([.. accountIds.Select(a => new AuthorCard(a, "h", "H", true))]);
        }

        public Task<IReadOnlyList<Guid>> Of(Guid reader, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([Author]);

        public List<Guid> Write(int count)
        {
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            var ids = Enumerable.Range(0, count).Select(i => Guid.CreateVersion7(start.AddSeconds(i))).ToList();
            Posts.AddRange(ids.Select(id => new PostCard(id, Author, "text", DateTimeOffset.UtcNow, null)));
            return ids;
        }
    }

    [Fact]
    public async Task A_missing_timeline_is_built_from_the_followed_accounts_and_read_newest_first()
    {
        var (store, timelines) = (new Store(), new InMemoryTimelines());
        var posts = store.Write(30);

        var page = await new ReadFeedHandler(timelines, store, store, store).Handle(new ReadFeed(Reader, 20), Ct);

        Assert.Equal(posts.AsEnumerable().Reverse().Take(20), page.Value.Items.Select(i => i.Post.PostId));
        Assert.True(page.Value.HasMore);
        Assert.Equal(1, timelines.Touched);
    }

    [Fact]
    public async Task One_round_is_two_batched_reads()
    {
        var (store, timelines) = (new Store(), new InMemoryTimelines());
        store.Write(30);

        await new ReadFeedHandler(timelines, store, store, store).Handle(new ReadFeed(Reader, 20), Ct);

        Assert.Equal((1, 1), (store.PostCalls, store.ProfileCalls));
    }

    [Fact]
    public async Task Drops_are_refilled_from_the_next_entries_for_at_most_three_rounds()
    {
        var (store, timelines) = (new Store(), new InMemoryTimelines());
        var posts = store.Write(100);
        var handler = new ReadFeedHandler(timelines, store, store, store);
        await handler.Handle(new ReadFeed(Reader, 20), Ct); // builds the timeline
        // The newest 70 are deleted after the build; their ids are still in the timeline.
        foreach (var post in posts.Skip(30))
        {
            store.Deleted.Add(post);
        }

        var page = await handler.Handle(new ReadFeed(Reader, 20), Ct);

        // Three rounds of 25 reach 75 entries deep: the 5 survivors among them, nothing older.
        Assert.Equal(posts.Skip(25).Take(5).Reverse(), page.Value.Items.Select(i => i.Post.PostId));
    }
}
