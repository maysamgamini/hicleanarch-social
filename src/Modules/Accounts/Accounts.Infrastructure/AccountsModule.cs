using System.Security.Claims;

using Accounts.Application;
using Accounts.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Identity;

namespace Accounts.Infrastructure;

/// <summary>
/// Everything a host needs to run the Accounts module: its services and its endpoints. The
/// monolith's host calls both; a host of its own, if the module is ever split off, would call the same two.
/// </summary>
public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddAccountsApplication()
            .AddSingleton<IAccountRepository, InMemoryAccountRepository>();

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
