using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Social.BuildingBlocks.Infrastructure.Events;
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
            .AddScoped<IFollowerPages, InProcessFollowerPages>();
    }

    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app) => app;
}
