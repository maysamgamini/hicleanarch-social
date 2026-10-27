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

    /// <summary>The key of the short-lived store's connection, for <c>[FromKeyedServices]</c>.</summary>
    public const string ShortLived = "valkey:short-lived";

    /// <summary>
    /// The short-lived store (locks, pull lists, suggestion lists, rate limits): a second Valkey, so the
    /// timeline store can evict by TTL (volatile-ttl) without ever choosing one of these keys over a
    /// timeline, and holds nothing else. <c>ConnectionStrings:ValkeyShort</c>; when it is not set (a
    /// developer's machine), the shared connection serves both.
    /// </summary>
    public static IServiceCollection AddShortLivedValkey(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSharedValkey(configuration);
        var shortLived = configuration.GetConnectionString("ValkeyShort");
        if (string.IsNullOrWhiteSpace(shortLived))
        {
            services.TryAddKeyedSingleton<IConnectionMultiplexer>(ShortLived, (sp, _) => sp.GetRequiredService<IConnectionMultiplexer>());
            return services;
        }

        services.TryAddKeyedSingleton<IConnectionMultiplexer>(ShortLived, (_, _) =>
        {
            var options = ConfigurationOptions.Parse(shortLived);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        return services;
    }

    /// <summary>Whether a Valkey connection is configured: a module picks its Valkey adapters or its in-memory ones.</summary>
    public static bool HasValkey(this IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Valkey"));
}
