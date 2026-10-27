using System.Text.Json;

using Accounts.Infrastructure;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Identity;

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

// The API speaks snake_case JSON: display_name, account_id.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddProblemDetails();
builder.Services.AddDispatcher();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTokenAuthentication(builder.Configuration);

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddAccountsModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapAccountsEndpoints();

await app.RunAsync();

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
