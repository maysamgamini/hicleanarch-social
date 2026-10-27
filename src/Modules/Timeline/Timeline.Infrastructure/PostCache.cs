using System.Diagnostics.Metrics;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Posts.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Valkey;

using StackExchange.Redis;

using Timeline.Application;

namespace Timeline.Infrastructure;

/// <summary>
/// The post summary cache: each hydrated post for 60 seconds as <c>post:{id}</c> in the short-lived
/// store, read for a whole page in one MGET; the misses go to Posts in one batch. A cache that cannot be
/// reached is a miss. PostEdited and PostDeleted evict the entry (<see cref="PostCacheEviction"/>).
/// </summary>
internal sealed class CachedPostSummaries(IPostSummaries posts, IConnectionMultiplexer valkey) : IPostSummaries
{
    public static readonly TimeSpan Keep = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RedisKey Key(Guid post) => "post:" + post.ToString("N");

    public async Task<IReadOnlyList<PostCard>> Get(IReadOnlyList<Guid> postIds, CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return [];
        }

        RedisValue[] cached;
        try
        {
            cached = await valkey.GetDatabase().StringGetAsync([.. postIds.Select(Key)]).WaitAsync(cancellationToken);
        }
        catch (RedisException)
        {
            return await posts.Get(postIds, cancellationToken);
        }

        var hits = cached.Where(v => !v.IsNull).Select(v => JsonSerializer.Deserialize<PostCard>(v.ToString(), Json)!).ToList();
        var missing = postIds.Where((_, i) => cached[i].IsNull).ToList();
        if (missing.Count == 0)
        {
            return hits;
        }

        var fresh = await posts.Get(missing, cancellationToken);
        try
        {
            var batch = valkey.GetDatabase().CreateBatch();
            var writes = fresh.Select(p => batch.StringSetAsync(Key(p.PostId), JsonSerializer.Serialize(p, Json), Keep)).ToList();
            batch.Execute();
            await Task.WhenAll(writes).WaitAsync(cancellationToken);
        }
        catch (RedisException)
        {
            // Not cached this time: the next page asks Posts again.
        }

        return [.. hits, .. fresh];
    }

    public Task<IReadOnlyList<PostCard>> RecentByAuthors(IReadOnlyList<Guid> authors, int perAuthor, CancellationToken cancellationToken) =>
        posts.RecentByAuthors(authors, perAuthor, cancellationToken);
}

/// <summary>
/// PostEdited and PostDeleted evict the post's cache entry at once, and again five seconds later: a
/// page that read Posts before the change and writes the cache after the first eviction would
/// otherwise keep the old text, or a deleted post, for the full 60 seconds. PostDeleted also starts the
/// post's removal from the timelines.
/// </summary>
internal sealed partial class PostCacheEviction(
    [FromKeyedServices(ValkeyRegistration.ShortLived)] IConnectionMultiplexer valkey, IDispatcher dispatcher, ILogger<PostCacheEviction> logger)
    : IIntegrationEventHandler<PostEdited>, IIntegrationEventHandler<PostDeleted>
{
    public static readonly TimeSpan Again = TimeSpan.FromSeconds(5);

    public Task Handle(PostEdited integrationEvent, CancellationToken cancellationToken) => Evict(integrationEvent.PostId, cancellationToken);

    public async Task Handle(PostDeleted integrationEvent, CancellationToken cancellationToken)
    {
        await Evict(integrationEvent.PostId, cancellationToken);
        var removal = await dispatcher.Send<RemoveDeletedPost, bool>(new RemoveDeletedPost(integrationEvent.PostId, integrationEvent.AuthorId), cancellationToken);
        if (!removal.IsSuccess)
        {
            throw new InvalidOperationException(removal.Errors[0].Code);
        }
    }

    private async Task Evict(Guid post, CancellationToken cancellationToken)
    {
        await valkey.GetDatabase().KeyDeleteAsync(CachedPostSummaries.Key(post)).WaitAsync(cancellationToken);
        // The second eviction runs on its own; lost if the task stops within the five seconds (the entry
        // then lives out its 60 seconds).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Again, CancellationToken.None);
                await valkey.GetDatabase().KeyDeleteAsync(CachedPostSummaries.Key(post));
            }
            catch (RedisException e)
            {
                LogFailed(logger, e, post);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The second eviction of post {PostId} from the cache failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid postId);
}

/// <summary>Read repair: the removal runs after the page is served, and a failure is only counted.</summary>
internal sealed class BackgroundReadRepair(ITimelines timelines) : IReadRepair
{
    private static readonly Counter<long> Repaired = new Meter(TimelineMetrics.MeterName).CreateCounter<long>(
        "social.timeline.read_repairs", "{post}", "Deleted posts a page found and removed from that reader's timeline");

    public void Remove(Guid reader, IReadOnlyList<Guid> postIds) => _ = Task.Run(async () =>
    {
        try
        {
            await timelines.Remove(reader, postIds, CancellationToken.None);
            Repaired.Add(postIds.Count);
        }
        catch (RedisException)
        {
            // The next page finds them again.
        }
    }, CancellationToken.None);
}
