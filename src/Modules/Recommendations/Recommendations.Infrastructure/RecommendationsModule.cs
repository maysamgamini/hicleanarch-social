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
using Recommendations.Infrastructure.Persistence;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Recommendations.Infrastructure;

/// <summary>Everything a host needs to run the Recommendations module. Nobody calls it; it only listens.</summary>
public static class RecommendationsModule
{
    public static IServiceCollection AddRecommendationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Recommendations")
            ?? throw new InvalidOperationException("ConnectionStrings:Recommendations is not set.");
        services.AddDbContext<RecommendationsDbContext>(db => RecommendationsDbOptions.Configure((DbContextOptionsBuilder<RecommendationsDbContext>)db, connectionString));
        services.AddSingleton<IModuleMigrator, RecommendationsMigrator>();
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

        return services
            .AddRecommendationsApplication()
            .AddScoped<SqlGraph>()
            .AddScoped<IGraphProjection>(sp => sp.GetRequiredService<SqlGraph>())
            .AddScoped<IGraphReads>(sp => sp.GetRequiredService<SqlGraph>());
    }

    public static IEndpointRouteBuilder MapRecommendationsEndpoints(this IEndpointRouteBuilder app)
    {
        var suggestions = app.MapGroup("/v1/suggestions").WithTags("Recommendations");

        suggestions.MapGet("/who-to-follow", async (ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Query<WhoToFollow, IReadOnlyList<Suggestion>>(new WhoToFollow(user.UserId()), ct);
            return result.IsSuccess ? Results.Ok(new SuggestionsReply(result.Value)) : result.Errors.ToProblem();
        });

        return app;
    }
}

internal sealed record SuggestionsReply(IReadOnlyList<Suggestion> Items);
