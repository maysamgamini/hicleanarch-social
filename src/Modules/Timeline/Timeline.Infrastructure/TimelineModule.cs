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
        app.MapGet("/v1/feed", async (int? limit, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var page = await dispatcher.Query<ReadFeed, FeedPage>(new ReadFeed(user.UserId(), limit ?? ReadFeedHandler.DefaultLimit), ct);
            return page.IsSuccess ? Results.Ok(FeedReply.Of(page.Value)) : page.Errors.ToProblem();
        }).WithTags("Timeline");

        return app;
    }
}

internal sealed record FeedReply(IReadOnlyList<FeedItemReply> Items, bool HasMore)
{
    public static FeedReply Of(FeedPage page) => new([.. page.Items.Select(FeedItemReply.Of)], page.HasMore);
}

internal sealed record AuthorReply(Guid AccountId, string Handle, string DisplayName);

internal sealed record FeedItemReply(Guid PostId, AuthorReply Author, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt)
{
    public static FeedItemReply Of(FeedItem item) => new(
        item.Post.PostId, new AuthorReply(item.Author.AccountId, item.Author.Handle, item.Author.DisplayName),
        item.Post.Text, item.Post.CreatedAt, item.Post.EditedAt);
}
