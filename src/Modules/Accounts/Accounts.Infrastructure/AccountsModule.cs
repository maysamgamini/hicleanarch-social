using System.Security.Claims;

using Accounts.Application;
using Accounts.Contracts;
using Accounts.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Accounts.Infrastructure;

/// <summary>
/// Everything a host needs to run the Accounts module: its services and its endpoints. The
/// monolith's host calls both; a host of its own, if the module is ever split off, would call the same two.
/// </summary>
public static class AccountsModule
{
    /// <summary>The module connects as its own database role, which can reach only the accounts schema.</summary>
    public static IServiceCollection AddAccountsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Accounts")
            ?? throw new InvalidOperationException("ConnectionStrings:Accounts is not set.");
        services.AddDbContext<AccountsDbContext>(db => AccountsDbOptions.Configure((DbContextOptionsBuilder<AccountsDbContext>)db, connectionString));
        services.AddSingleton<IModuleMigrator, AccountsMigrator>();
        services.AddSingleton(new ModuleDatabase("Accounts", AccountsDbContext.Schema, connectionString));
        return services
            .AddAccountsApplication()
            .AddScoped<IAccountRepository, EfAccountRepository>();
    }

    public static IEndpointRouteBuilder MapAccountsEndpoints(this IEndpointRouteBuilder app)
    {
        // Register or update the signed-in person's profile. PUT: sending it twice ends in the same state.
        app.MapPut("/v1/me", async (ProfileRequest request, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send<UpdateMyProfile, ProfileUpdate>(
                new UpdateMyProfile(user.UserId(), request.Handle, request.DisplayName), ct);
            if (!result.IsSuccess)
            {
                return result.Errors.ToProblem();
            }

            var reply = ProfileReply.From(result.Value.Profile);
            return result.Value.Registered ? Results.Created($"/v1/accounts/{reply.Handle}", reply) : Results.Ok(reply);
        }).WithTags("Accounts");

        // Anyone may read a profile, signed in or not.
        app.MapGet("/v1/accounts/{handle}", async (string handle, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Query<GetProfileByHandle, Profile>(new GetProfileByHandle(handle), ct);
            return result.IsSuccess ? Results.Ok(ProfileReply.From(result.Value)) : result.Errors.ToProblem();
        }).AllowAnonymous().WithTags("Accounts");

        return app;
    }
}

/// <summary>The JSON body of <c>PUT /v1/me</c>. Missing fields arrive as null and are refused by the rules.</summary>
internal sealed record ProfileRequest(string? Handle, string? DisplayName);

internal sealed record ProfileReply(Guid AccountId, string Handle, string DisplayName)
{
    public static ProfileReply From(Profile profile) => new(profile.AccountId, profile.Handle, profile.DisplayName);
}
