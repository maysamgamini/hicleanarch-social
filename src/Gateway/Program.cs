using Social.BuildingBlocks.Infrastructure.Hosting;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.Gateway;

var builder = WebApplication.CreateBuilder(args);

// One catch-all route to the monolith ("ReverseProxy" in appsettings.json). Moving a module out of
// the monolith is a new cluster and a route here, and nothing else (Stages 4 and 5).
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// The token is checked as the monolith checks it, only to know whose request it is: the limits for
// posting and following are per account (the token's subject), so many people behind one address
// are not limited together. The monolith still checks every token itself.
builder.Services.AddTokenAuthentication(builder.Configuration);
builder.Services.AddRateLimits(builder.Configuration);

// X-Forwarded-For is trusted only from the load balancer, inside the VPC (ForwardedHeaders:KnownNetwork);
// a client's own header from anywhere else is ignored.
builder.Services.AddProxyHeaders(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/gateway/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// The limits run in the proxy pipeline: for every request a route forwards, and for nothing else.
// Who may call what is the monolith's decision, so the proxy itself lets every request through.
app.MapReverseProxy(proxy =>
{
    proxy.UseMiddleware<RateLimitMiddleware>();
    proxy.UseSessionAffinity();
    proxy.UseLoadBalancing();
    proxy.UsePassiveHealthChecks();
}).AllowAnonymous();

await app.RunAsync();

/// <summary>Visible to the tests, which start the gateway in memory.</summary>
public partial class Program;
