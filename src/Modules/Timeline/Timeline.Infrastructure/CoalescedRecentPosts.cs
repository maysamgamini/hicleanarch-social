using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Timeline.Application;

namespace Timeline.Infrastructure;

/// <summary>
/// A read storm on one pulled author costs about one query per task per second. Each task keeps each
/// pulled author's newest posts for a second (the micro-cache); from one to three seconds old they are
/// still served while one refresh runs in the background (stale-while-revalidate), so a slow refresh
/// never holds a reader; older or missing, readers wait for one load between them (single-flight).
/// Only a page's first read of an author is shared: a later page (a cursor) goes straight to Posts.
/// </summary>
internal sealed class CoalescedRecentPosts(IRecentPosts source, TimeProvider clock) : IRecentPosts
{
    public const string Source = "Timeline:RecentPosts:Source";

    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);

    /// <summary>A refresh is given this long; a reader never waits for it once there is something to serve.</summary>
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Enough for the largest page (50) and its 5 spare.</summary>
    public const int Depth = ReadFeedHandler.MaxLimit + 5;

    private static readonly Counter<long> Loads = new Meter("Social.Timeline").CreateCounter<long>(
        "social.feed.pull_loads", "{query}", "Queries to Posts for a pulled author's newest posts, after coalescing");

    private readonly ConcurrentDictionary<Guid, Slot> _slots = new();

    private sealed class Slot
    {
        public IReadOnlyList<PostCard>? Posts { get; set; }

        public DateTimeOffset At { get; set; }

        public Task<IReadOnlyList<PostCard>>? Loading { get; set; }
    }

    public async Task<IReadOnlyList<PostCard>> Of(Guid author, Guid? before, int limit, CancellationToken cancellationToken)
    {
        if (before is not null || limit > Depth)
        {
            return await source.Of(author, before, limit, cancellationToken);
        }

        var slot = _slots.GetOrAdd(author, _ => new Slot());
        Task<IReadOnlyList<PostCard>> load;
        lock (slot)
        {
            var age = clock.GetUtcNow() - slot.At;
            if (slot.Posts is { } cached && age < Stale)
            {
                if (age >= Fresh)
                {
                    slot.Loading ??= Load(author, slot); // revalidate in the background; serve what is here
                }

                return Take(cached, limit);
            }

            load = slot.Loading ??= Load(author, slot);
        }

        return Take(await load.WaitAsync(cancellationToken), limit);
    }

    private static List<PostCard> Take(IReadOnlyList<PostCard> posts, int limit) => [.. posts.Take(limit)];

    // Runs on its own, not on any reader's token: a reader that gives up does not cancel the others' load.
    private Task<IReadOnlyList<PostCard>> Load(Guid author, Slot slot) => Task.Run(async () =>
    {
        try
        {
            using var timeout = new CancellationTokenSource(RefreshTimeout);
            Loads.Add(1);
            var posts = await source.Of(author, null, Depth, timeout.Token);
            lock (slot)
            {
                slot.Posts = posts;
                slot.At = clock.GetUtcNow();
            }

            return posts;
        }
        finally
        {
            lock (slot)
            {
                slot.Loading = null;
            }
        }
    });
}
