using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Claims;

using StackExchange.Redis;

namespace Social.Gateway;

/// <summary>Requests per minute: posting and following per account, anonymous reads per address (Gateway:Limits).</summary>
public sealed class RateLimitOptions
{
    public const string Section = "Gateway:Limits";

    public int PostsPerMinute { get; set; } = 20;

    public int FollowsPerMinute { get; set; } = 60;

    public int AnonymousReadsPerMinute { get; set; } = 300;

    /// <summary>The counters' key prefix in the short-lived store.</summary>
    public string Prefix { get; set; } = "rl";
}

public static class RateLimitRegistration
{
    public static IServiceCollection AddRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.Section))
            .Validate(o => o.PostsPerMinute > 0 && o.FollowsPerMinute > 0 && o.AnonymousReadsPerMinute > 0, "Gateway:Limits must be positive.")
            .ValidateOnStart();
        // The counters live in the short-lived store, shared by every gateway task. abortConnect=false:
        // the gateway starts, and lets requests through, while Valkey is down.
        var valkey = configuration.GetConnectionString("ValkeyShort") ?? configuration.GetConnectionString("Valkey")
            ?? throw new InvalidOperationException("ConnectionStrings:ValkeyShort is not set: the rate limits are counted there.");
        var options = ConfigurationOptions.Parse(valkey);
        options.AbortOnConnectFail = false;
        options.AsyncTimeout = 250;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}

/// <summary>
/// A fixed one-minute window per policy and caller, counted in Valkey: one INCR (and the window's
/// expiry with the first). Above the limit: 429 with Retry-After, the seconds left in the window. A
/// Valkey that cannot be reached lets the request through and counts it (social.gateway.limit_unavailable).
/// </summary>
internal sealed class RateLimitMiddleware(RequestDelegate next, IConnectionMultiplexer valkey, Microsoft.Extensions.Options.IOptions<RateLimitOptions> options)
{
    private static readonly Counter<long> Limited = new Meter("Social.Gateway").CreateCounter<long>("social.gateway.limited", "{request}", "Requests refused with 429");

    private static readonly Counter<long> Unavailable = new Meter("Social.Gateway").CreateCounter<long>(
        "social.gateway.limit_unavailable", "{request}", "Requests let through because the counters could not be reached");

    private const string Count = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return {n, redis.call('PTTL', KEYS[1])}
        """;

    public async Task InvokeAsync(HttpContext context)
    {
        if (Policy(context) is not { } policy)
        {
            await next(context);
            return;
        }

        RedisResult[] counted;
        try
        {
            counted = (RedisResult[])(await valkey.GetDatabase().ScriptEvaluateAsync(Count,
                [$"{options.Value.Prefix}:{policy.Name}:{policy.Caller}"], [(long)TimeSpan.FromMinutes(1).TotalMilliseconds]))!;
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            Unavailable.Add(1);
            await next(context);
            return;
        }

        if ((long)counted[0] <= policy.Limit)
        {
            await next(context);
            return;
        }

        Limited.Add(1, new KeyValuePair<string, object?>("policy", policy.Name));
        var seconds = Math.Max(1, (int)Math.Ceiling((long)counted[1] / 1000.0));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new { reason = "rate_limited", retry_after_seconds = seconds });
    }

    /// <summary>Which limit a request counts against, and as whom; null when none applies.</summary>
    private (string Name, string Caller, int Limit)? Policy(HttpContext context)
    {
        var limits = options.Value;
        var request = context.Request;
        var account = context.User.FindFirstValue("sub");
        if (account is not null)
        {
            if (HttpMethods.IsPost(request.Method) && request.Path.Equals("/v1/posts", StringComparison.OrdinalIgnoreCase))
            {
                return ("post", account, limits.PostsPerMinute);
            }

            if ((HttpMethods.IsPut(request.Method) || HttpMethods.IsDelete(request.Method))
                && request.Path.StartsWithSegments("/v1/accounts", StringComparison.OrdinalIgnoreCase)
                && request.Path.Value!.EndsWith("/follow", StringComparison.OrdinalIgnoreCase))
            {
                return ("follow", account, limits.FollowsPerMinute);
            }

            return null;
        }

        // Without a valid token: reads, per address (the load balancer's X-Forwarded-For, or the peer).
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return HttpMethods.IsGet(request.Method) ? ("read", address, limits.AnonymousReadsPerMinute) : null;
    }
}
