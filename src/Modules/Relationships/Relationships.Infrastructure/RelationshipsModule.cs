using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Relationships.Application;
using Relationships.Infrastructure.Persistence;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Relationships.Infrastructure;

/// <summary>Everything a host needs to run the Relationships module: its services and its endpoints.</summary>
public static class RelationshipsModule
{
    /// <summary>The module connects as its own database role, which can reach only the relationships schema.</summary>
    public static IServiceCollection AddRelationshipsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Relationships")
            ?? throw new InvalidOperationException("ConnectionStrings:Relationships is not set.");
        services.AddDbContext<RelationshipsDbContext>(db => RelationshipsDbOptions.Configure((DbContextOptionsBuilder<RelationshipsDbContext>)db, connectionString));
        services.AddKeyedScoped<ITransactionScope>(ModuleName.Value, (sp, _) => new ModuleTransaction<RelationshipsDbContext>(sp.GetRequiredService<RelationshipsDbContext>()));
        services.AddSingleton<IModuleMigrator, RelationshipsMigrator>();
        services.AddSingleton(new ModuleDatabase("Relationships", RelationshipsDbContext.Schema, connectionString));
        return services
            .AddRelationshipsApplication()
            .AddScoped<IRelationshipStore, SqlRelationshipStore>()
            .AddScoped<IRelationshipReads, SqlRelationshipReads>()
            .AddScoped<IAccountDirectory, AccountDirectory>();
    }

    public static IEndpointRouteBuilder MapRelationshipsEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/v1/accounts/{id:guid}").WithTags("Relationships");

        // PUT and DELETE: the end state is a fact, so sending either twice changes nothing more.
        account.MapPut("/follow", (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(dispatcher.Send<Follow, FollowState>(new Follow(user.UserId(), id), ct)));
        account.MapDelete("/follow", (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(dispatcher.Send<Unfollow, FollowState>(new Unfollow(user.UserId(), id), ct)));

        account.MapPut("/block", (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(dispatcher.Send<Block, BlockState>(new Block(user.UserId(), id), ct)));
        account.MapDelete("/block", (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(dispatcher.Send<Unblock, BlockState>(new Unblock(user.UserId(), id), ct)));
        account.MapPut("/mute", (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(dispatcher.Send<Mute, MuteState>(new Mute(user.UserId(), id), ct)));
        account.MapDelete("/mute", (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Reply(dispatcher.Send<Unmute, MuteState>(new Unmute(user.UserId(), id), ct)));

        account.MapGet("/followers", async (Guid id, Guid? after, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            PageReply.From(await dispatcher.Query<ListFollowers, RelationshipPage>(new ListFollowers(id, after, PageSize.Clamp(limit)), ct)));
        account.MapGet("/following", async (Guid id, Guid? after, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            PageReply.From(await dispatcher.Query<ListFollowing, RelationshipPage>(new ListFollowing(id, after, PageSize.Clamp(limit)), ct)));

        return app;
    }

    private static async Task<IResult> Reply<T>(Task<Social.BuildingBlocks.Domain.Result<T>> sent)
    {
        var result = await sent;
        return result.IsSuccess ? Results.Ok(result.Value) : result.Errors.ToProblem();
    }
}

internal sealed record PageReply(IReadOnlyList<ListedAccount> Items, Guid? NextAfter)
{
    public static IResult From(Social.BuildingBlocks.Domain.Result<RelationshipPage> page) =>
        page.IsSuccess ? Results.Ok(new PageReply(page.Value.Items, page.Value.Next)) : page.Errors.ToProblem();
}
