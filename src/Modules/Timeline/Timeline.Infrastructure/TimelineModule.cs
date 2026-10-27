using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Valkey;

using Timeline.Application;

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
        var keys = configuration.GetSection("Timeline:Cursor:Keys");
        services.AddSingleton(new FeedCursors(new CursorKeys(
            keys["Current"] ?? throw new InvalidOperationException("Timeline:Cursor:Keys:Current is not set: feed cursors are signed with it."),
            keys["Previous"])));
        // Adding a member that is already there changes nothing, so the fanout needs no inbox.
        services.AddModuleWithoutInbox(ModuleName.Value);
        services.AddEventHandler<PostPublished, FanOutOnPostPublished>(ModuleName.Value);
        return services
            .AddTimelineApplication()
            .AddSingleton<ITimelines, ValkeyTimelines>()
            .AddScoped<IFollowerPages, InProcessFollowerPages>()
            .AddScoped<IPostSummaries, InProcessPostSummaries>()
            .AddScoped<IProfiles, InProcessProfiles>()
            .AddScoped<IFollowing, InProcessFollowing>();
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

        return app;
    }
}

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
