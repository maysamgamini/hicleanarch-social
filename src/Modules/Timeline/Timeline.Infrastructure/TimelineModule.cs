using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Hosting;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;
using Social.BuildingBlocks.Infrastructure.Valkey;

using Microsoft.EntityFrameworkCore;

using StackExchange.Redis;

using Timeline.Application;
using Timeline.Domain;
using Timeline.Infrastructure.Persistence;

namespace Timeline.Infrastructure;

/// <summary>Everything a host needs to run the Timeline module.</summary>
public static class TimelineModule
{
    /// <summary>Timelines live in Valkey (ConnectionStrings:Valkey): derived data, read hundreds of times for each write.</summary>
    public static IServiceCollection AddTimelineModule(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.HasValkey())
        {
            throw new InvalidOperationException("ConnectionStrings:Valkey is not set: Timeline keeps its timelines there.");
        }

        services.AddShortLivedValkey(configuration);
        var connectionString = configuration.GetConnectionString("Timeline")
            ?? throw new InvalidOperationException("ConnectionStrings:Timeline is not set.");
        services.AddDbContext<TimelineDbContext>(db => TimelineDbOptions.Configure((DbContextOptionsBuilder<TimelineDbContext>)db, connectionString));
        services.AddSingleton<IModuleMigrator, TimelineMigrator>();
        services.AddHostedService<TimelineMonitor>();
        // The fanout's worker loops, in two pools; given back their jobs at a graceful stop.
        services.AddSingleton<FanoutPace>();
        services.AddSingleton<IFanoutPace>(sp => sp.GetRequiredService<FanoutPace>());
        services.AddHostedService(sp => sp.GetRequiredService<FanoutPace>());
        services.AddSingleton<FanoutWorkers>();
        services.AddHostedService(sp => sp.GetRequiredService<FanoutWorkers>());
        services.AddSingleton<IStopParticipant>(sp => sp.GetRequiredService<FanoutWorkers>());
        services.AddHostedService<RebuildWorkers>();
        services.AddHostedService<TimelineConversion>();
        services.AddSingleton(new ModuleDatabase("Timeline", TimelineDbContext.Schema, connectionString));
        var keys = configuration.GetSection("Timeline:Cursor:Keys");
        services.AddSingleton(new FeedCursors(new CursorKeys(
            keys["Current"] ?? throw new InvalidOperationException("Timeline:Cursor:Keys:Current is not set: feed cursors are signed with it."),
            keys["Previous"])));
        // Adding a member that is already there changes nothing, so the fanout needs no inbox.
        services.AddModuleWithoutInbox(ModuleName.Value);
        services.AddEventHandler<PostPublished, FanOutOnPostPublished>(ModuleName.Value);
        // Best-effort upkeep: the read filter is what guarantees a page; these only clean the timelines.
        services.AddEventHandler<AccountBlocked, TimelineUpkeep>(ModuleName.Value);
        services.AddEventHandler<FollowEnded, TimelineUpkeep>(ModuleName.Value);
        services.AddEventHandler<FollowStarted, TimelineUpkeep>(ModuleName.Value);
        // Hybrid fanout: the registry of pulled authors follows the counts Relationships announces.
        services.AddEventHandler<FollowerCountChanged, RegistryOnFollowerCount>(ModuleName.Value);
        // A deleted or edited post leaves the cache, and a deleted one the timelines (s3-l09).
        services.AddEventHandler<PostEdited, PostCacheEviction>(ModuleName.Value);
        services.AddEventHandler<PostDeleted, PostCacheEviction>(ModuleName.Value);
        services.AddSingleton(new PullOptions(configuration.GetValue("Timeline:Pull:Budget", PullRules.Budget)));
        return services
            .AddTimelineApplication()
            .AddSingleton<ITimelines, ValkeyTimelines>()
            .AddScoped<IFollowerPages, InProcessFollowerPages>()
            // Posts' answer behind the 60-second post summary cache in the short-lived store (s3-l09).
            .AddScoped<IPostSummaries>(sp => new CachedPostSummaries(
                new InProcessPostSummaries(sp.GetRequiredService<IDispatcher>()),
                sp.GetRequiredKeyedService<IConnectionMultiplexer>(ValkeyRegistration.ShortLived)))
            .AddSingleton<IReadRepair, BackgroundReadRepair>()
            .AddScoped<IProfiles, InProcessProfiles>()
            .AddScoped<IFollowing, InProcessFollowing>()
            .AddScoped<IHiddenAuthors, InProcessHiddenAuthors>()
            .AddScoped<IFanoutProgress, SqlFanoutProgress>()
            .AddScoped<IFanoutJobs, SqlFanoutJobs>()
            .AddScoped<IAudience, InProcessAudience>()
            .AddScoped<IRebuildJobs, SqlRebuildJobs>()
            .AddSingleton<IRebuildLock, ValkeyRebuildLock>()
            .AddScoped<IAuthorActivity, InProcessAuthorActivity>()
            .AddSingleton<ValkeyPulls>()
            .AddSingleton<IPullRegistry>(sp => sp.GetRequiredService<ValkeyPulls>())
            .AddSingleton<IPullLists>(sp => sp.GetRequiredService<ValkeyPulls>())
            // Posts' answer, behind the per-task single-flight and micro-cache.
            .AddKeyedSingleton<IRecentPosts, InProcessRecentPosts>(CoalescedRecentPosts.Source)
            .AddSingleton<IRecentPosts>(sp => new CoalescedRecentPosts(sp.GetRequiredKeyedService<IRecentPosts>(CoalescedRecentPosts.Source), sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<ISeenPosts, SampledSeenPosts>();
    }

    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/feed", async (int? limit, string? cursor, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var page = await dispatcher.Query<ReadFeed, FeedPage>(new ReadFeed(user.UserId(), limit ?? ReadFeedHandler.DefaultLimit, cursor), ct);
            TimelineMetrics.FeedDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return page.IsSuccess
                ? Results.Ok(FeedReply.Of(page.Value))
                // A bad cursor is a malformed request, not a rule broken: 400.
                : page.Errors.ToProblem(page.Errors[0].Code == FeedCursors.Invalid.Code ? StatusCodes.Status400BadRequest : null);
        }).WithTags("Timeline");

        // Which acknowledgments a post has reached, for its author: stored, then fanned out.
        app.MapGet("/v1/posts/{id:guid}/delivery", async (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var delivery = await dispatcher.Query<GetDelivery, Delivery>(new GetDelivery(id, user.UserId()), ct);
            return delivery.IsSuccess
                ? Results.Ok(new DeliveryReply(delivery.Value.PostId, delivery.Value.StoredAt, delivery.Value.FannedOutAt, delivery.Value.FollowersReached))
                : delivery.Errors.ToProblem();
        }).WithTags("Timeline");

        return app;
    }
}

internal sealed record DeliveryReply(Guid PostId, DateTimeOffset StoredAt, DateTimeOffset? FannedOutAt, long FollowersReached);

/// <summary><c>partial</c>: a source of the page did not answer in time, so it may lack posts that are there (s3-l17).</summary>
internal sealed record FeedReply(IReadOnlyList<FeedItemReply> Items, string? NextCursor, bool HasMore, bool Partial)
{
    public static FeedReply Of(FeedPage page) => new([.. page.Items.Select(FeedItemReply.Of)], page.NextCursor, page.NextCursor is not null, page.Partial);
}

internal sealed record AuthorReply(Guid AccountId, string Handle, string DisplayName);

internal sealed record FeedItemReply(Guid PostId, AuthorReply Author, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, IReadOnlyList<string> Media)
{
    public static FeedItemReply Of(FeedItem item) => new(
        item.Post.PostId, new AuthorReply(item.Author.AccountId, item.Author.Handle, item.Author.DisplayName),
        item.Post.Text, item.Post.CreatedAt, item.Post.EditedAt, item.Post.Media ?? []);
}
