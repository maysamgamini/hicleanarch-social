using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Social.BuildingBlocks.Infrastructure.Identity;

namespace Integration.Tests;

/// <summary>The host, started in memory, and clients signed in as people of the test's choosing.</summary>
public static class TestHost
{
    public const string ClientId = "test-client";

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    /// <summary>A token for the given person, shaped like a Cognito access token.</summary>
    public static string Token(Guid userId) => LocalTokens.Issue(SigningKey, ClientId, userId);

    public static WebApplicationFactory<Program> Start() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseEnvironment("Testing")
            .UseSetting("Auth:ClientId", ClientId)
            .UseSetting("Auth:LocalSigningKey", SigningKey));

    /// <summary>A client signed in as <paramref name="userId"/>, or anonymous when it is null.</summary>
    public static HttpClient Client(WebApplicationFactory<Program> host, Guid? userId = null)
    {
        var client = host.CreateClient();
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(id));
        }

        return client;
    }
}
