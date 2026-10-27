using Accounts.Contracts;

using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;

namespace Accounts.Application;

public static class AccountsApplication
{
    public static IServiceCollection AddAccountsApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<UpdateMyProfile, ProfileUpdate>, UpdateMyProfileHandler>()
            .AddScoped<IQueryHandler<GetProfileByHandle, Profile>, GetProfileByHandleHandler>()
            .AddScoped<IQueryHandler<GetProfiles, IReadOnlyList<Profile>>, GetProfilesHandler>();
}
