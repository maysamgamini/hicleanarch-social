using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using StackExchange.Redis;

namespace Social.BuildingBlocks.Infrastructure.Valkey;

public static class ValkeyRegistration
{
    /// <summary>
    /// The shared Valkey connection, when <c>ConnectionStrings:Valkey</c> is set; nothing otherwise.
    /// Every module that needs it calls this; the first call registers.
    /// </summary>
    public static IServiceCollection AddSharedValkey(this IServiceCollection services, IConfiguration configuration)
    {
        var valkey = configuration.GetConnectionString("Valkey");
        if (string.IsNullOrWhiteSpace(valkey))
        {
            return services;
        }

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(valkey);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        return services;
    }

    /// <summary>Whether a Valkey connection is configured: a module picks its Valkey adapters or its in-memory ones.</summary>
    public static bool HasValkey(this IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Valkey"));
}
