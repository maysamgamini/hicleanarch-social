using System.Text.Json;

using Accounts.Infrastructure;

using Posts.Infrastructure;

using Relationships.Infrastructure;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Hosting;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;
using Social.Host;

using Timeline.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// `dotnet run -- token` prints a local token for development: a new user, signed with the local key.
// It runs before any service is built, so standard output carries the token and nothing else.
// Anywhere but Development it refuses, on standard error.
if (args.FirstOrDefault() == "token")
{
    if (!builder.Environment.IsDevelopment())
    {
        await Console.Error.WriteLineAsync($"The token command runs only in Development; this is {builder.Environment.EnvironmentName}.");
        Environment.ExitCode = 2;
        return;
    }

    var auth = builder.Configuration.GetSection("Auth");
    var user = args.Length > 1 && Guid.TryParse(args[1], out var given) ? given : Guid.NewGuid();
    Console.WriteLine(LocalTokens.Issue(auth["LocalSigningKey"]!, auth["ClientId"]!, user));
    return;
}

builder.Logging.AddTraceCorrelatedConsole();

// The API speaks snake_case JSON: display_name, account_id.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddProblemDetails();
// A database that cannot be reached is a 503 the client may retry, not a 500.
builder.Services.AddExceptionHandler<DatabaseUnavailableHandler>();
builder.Services.AddDispatcher();
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTelemetry(builder.Configuration);
// Cognito's tokens when Auth:Authority is set (on AWS); the local issuer's otherwise (development, tests).
builder.Services.AddTokenAuthentication(builder.Configuration);
// Events between modules: the outboxes, the in-process bus and the background dispatcher.
builder.Services.AddInProcessEvents();
builder.Services.AddProxyHeaders(builder.Configuration);
builder.Services.AddGracefulStop();
builder.Services.AddHealth(builder.Configuration);

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddAccountsModule(builder.Configuration);
builder.Services.AddRelationshipsModule(builder.Configuration);
builder.Services.AddPostsModule(builder.Configuration);
builder.Services.AddTimelineModule(builder.Configuration);

var app = builder.Build();

// `dotnet Social.Host.dll migrate` prepares the database and exits: the deploy's migration step,
// run once per release as the schema owner before new tasks start, never part of start-up.
if (args.FirstOrDefault() == "migrate")
{
    var owner = app.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set.");
    await app.Services.GetRequiredService<DatabaseMigrator>().Run(owner, CancellationToken.None);
    return;
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealth();
app.MapAccountsEndpoints();
app.MapRelationshipsEndpoints();
app.MapPostsEndpoints();
app.MapTimelineEndpoints();

await app.RunAsync();

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
