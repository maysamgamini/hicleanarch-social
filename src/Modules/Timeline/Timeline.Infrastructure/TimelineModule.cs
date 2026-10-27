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
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;
using Social.BuildingBlocks.Infrastructure.Valkey;

using Microsoft.EntityFrameworkCore;

using Timeline.Application;
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

        services.AddSharedValkey(configuration);
        var connectionString = configuration.GetConnectionString("Timeline")
            ?? throw new InvalidOperationException("ConnectionStrings:Timeline is not set.");
        services.AddDbContext<TimelineDbContext>(db => TimelineDbOptions.Configure((DbContextOptionsBuilder<TimelineDbContext>)db, connectionString));
        services.AddSingleton<IModuleMigrator, TimelineMigrator>();
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
        return services
            .AddTimelineApplication()
            .AddSingleton<ITimelines, ValkeyTimelines>()
            .AddScoped<IFollowerPages, InProcessFollowerPages>()
            .AddScoped<IPostSummaries, InProcessPostSummaries>()
            .AddScoped<IProfiles, InProcessProfiles>()
            .AddScoped<IFollowing, InProcessFollowing>()
            .AddScoped<IHiddenAuthors, InProcessHiddenAuthors>()
            .AddScoped<IFanoutProgress, SqlFanoutProgress>()
            .AddSingleton<ISeenPosts, SampledSeenPosts>();
    }

    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/feed", async (int? limit, string? cursor, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var page = await dispatcher.Query<ReadFeed, FeedPage>(new ReadFeed(user.UserId(), limit ?? ReadFeedHandler.DefaultLimit, cursor), ct);
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

internal sealed record FeedReply(IReadOnlyList<FeedItemReply> Items, string? NextCursor, bool HasMore)
{
    public static FeedReply Of(FeedPage page) => new([.. page.Items.Select(FeedItemReply.Of)], page.NextCursor, page.NextCursor is not null);
}

internal sealed record AuthorReply(Guid AccountId, string Handle, string DisplayName);

internal sealed record FeedItemReply(Guid PostId, AuthorReply Author, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt)
{
    public static FeedItemReply Of(FeedItem item) => new(
        item.Post.PostId, new AuthorReply(item.Author.AccountId, item.Author.Handle, item.Author.DisplayName),
        item.Post.Text, item.Post.CreatedAt, item.Post.EditedAt);
}
