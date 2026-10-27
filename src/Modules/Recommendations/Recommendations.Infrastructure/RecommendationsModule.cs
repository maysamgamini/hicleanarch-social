using System.Security.Claims;

using Accounts.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Recommendations.Application;
using Recommendations.Domain;
using Neo4j.Driver;

using Recommendations.Infrastructure.Graph;
using Recommendations.Infrastructure.Persistence;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;
using Social.BuildingBlocks.Infrastructure.Valkey;

namespace Recommendations.Infrastructure;

/// <summary>Everything a host needs to run the Recommendations module. Nobody calls it; it only listens.</summary>
public static class RecommendationsModule
{
    public static IServiceCollection AddRecommendationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Recommendations")
            ?? throw new InvalidOperationException("ConnectionStrings:Recommendations is not set.");
        services.AddDbContext<RecommendationsDbContext>(db => RecommendationsDbOptions.Configure((DbContextOptionsBuilder<RecommendationsDbContext>)db, connectionString));
        // Bolt to Neo4j locally (bolt://), to Neptune over TLS on AWS (bolt+s://…:8182). The sandbox
        // uses no IAM database authentication: Graph:Auth is "none".
        var graphUri = configuration["Graph:Uri"];
        if (string.IsNullOrEmpty(graphUri))
        {
            throw new InvalidOperationException("Graph:Uri is not set: Recommendations keeps its graph in a graph database.");
        }

        services.AddSingleton(_ => GraphDatabase.Driver(graphUri, GraphAuth(configuration)));
        services.AddSingleton<IModuleMigrator>(sp => new RecommendationsMigrator(sp.GetRequiredService<IDriver>()));

        services.AddSingleton(new ModuleDatabase("Recommendations", RecommendationsDbContext.Schema, connectionString));

        // Versioned edges make every handler safe to repeat: no inbox.
        services.AddModuleWithoutInbox(ModuleName.Value);
        services.AddEventHandler<AccountRegistered, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<ProfileUpdated, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<AccountDeactivated, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<FollowStarted, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<FollowEnded, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<AccountBlocked, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<AccountUnblocked, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<AccountMuted, GraphProjectionHandlers>(ModuleName.Value);
        services.AddEventHandler<AccountUnmuted, GraphProjectionHandlers>(ModuleName.Value);

        services.AddHostedService<GraphMaintenance>();
        if (!configuration.HasValkey())
        {
            throw new InvalidOperationException("ConnectionStrings:Valkey is not set: suggestions are cached there.");
        }

        services.AddSharedValkey(configuration);
        return services
            .AddRecommendationsApplication()
            .AddSingleton<Neo4jGraph>()
            .AddSingleton<IGraphProjection>(sp => sp.GetRequiredService<Neo4jGraph>())
            .AddSingleton<IGraphReads>(sp => sp.GetRequiredService<Neo4jGraph>())
            .AddSingleton<IGraphMaintenance>(sp => sp.GetRequiredService<Neo4jGraph>())
            .AddScoped<IRelationshipSource, InProcessRelationshipSource>()
            .AddScoped<IDismissals, SqlDismissals>()
            .AddSingleton<ISuggestionCache, ValkeySuggestionCache>();
    }

    private static IAuthToken GraphAuth(IConfiguration configuration) =>
        configuration["Graph:Auth"] switch
        {
            null or "" or "none" => AuthTokens.None,
            "basic" => AuthTokens.Basic(configuration["Graph:User"], configuration["Graph:Password"]),
            var other => throw new InvalidOperationException($"Graph:Auth {other} is not supported: none or basic."),
        };

    public static IEndpointRouteBuilder MapRecommendationsEndpoints(this IEndpointRouteBuilder app)
    {
        var suggestions = app.MapGroup("/v1/suggestions").WithTags("Recommendations");

        suggestions.MapGet("/who-to-follow", async (ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(await dispatcher.Query<WhoToFollow, IReadOnlyList<Suggestion>>(new WhoToFollow(user.UserId()), ct)));

        suggestions.MapGet("/people-you-may-know", async (ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(await dispatcher.Query<PeopleYouMayKnow, IReadOnlyList<Suggestion>>(new PeopleYouMayKnow(user.UserId()), ct)));

        suggestions.MapPost("/{accountId:guid}/dismiss", async (Guid accountId, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send<Dismiss, DateTimeOffset>(new Dismiss(user.UserId(), accountId), ct);
            return result.IsSuccess ? Results.Ok(new DismissReply(accountId, result.Value)) : result.Errors.ToProblem();
        });

        return app;
    }

    private static IResult Reply(Social.BuildingBlocks.Domain.Result<IReadOnlyList<Suggestion>> result) =>
        result.IsSuccess ? Results.Ok(new SuggestionsReply(result.Value)) : result.Errors.ToProblem();
}

internal sealed record SuggestionsReply(IReadOnlyList<Suggestion> Items);

internal sealed record DismissReply(Guid AccountId, DateTimeOffset DismissedUntil);
