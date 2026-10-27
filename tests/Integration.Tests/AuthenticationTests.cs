using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Social.BuildingBlocks.Infrastructure.Identity;

namespace Integration.Tests;

/// <summary>
/// Who is calling: the API checks every token without calling the identity provider. Locally the
/// issuer is the vendored local one; on AWS it is Cognito, configured by Auth:Authority. Either way a
/// token from anyone else is refused.
/// </summary>
public sealed class AuthenticationTests(Postgres postgres) : IntegrationTest(postgres)
{
    private async Task<HttpStatusCode> RegisterWith(string? token)
    {
        var client = StartHost().CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var reply = await client.PutAsJsonAsync("/v1/me", new { handle = "someone", display_name = "Someone" }, Ct);
        return reply.StatusCode;
    }

    private static string Signed(string issuer, string key, string tokenUse = "access", string clientId = TestApp.ClientId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(LocalTokens.SigningKey(key), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["client_id"] = clientId,
                ["token_use"] = tokenUse,
            },
        });

    [Fact]
    public async Task No_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await RegisterWith(null));

    [Fact]
    public async Task A_token_from_another_issuer_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            await RegisterWith(Signed("https://cognito-idp.us-east-1.amazonaws.com/us-east-1_SomeoneElse", TestApp.SigningKey)));

    [Fact]
    public async Task A_token_signed_with_another_key_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await RegisterWith(Signed(LocalTokens.Issuer, "another-key-that-is-long-enough-0123456789")));

    [Fact]
    public async Task An_id_token_or_a_token_for_another_app_client_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await RegisterWith(Signed(LocalTokens.Issuer, TestApp.SigningKey, tokenUse: "id")));
        Assert.Equal(HttpStatusCode.Unauthorized, await RegisterWith(Signed(LocalTokens.Issuer, TestApp.SigningKey, clientId: "another-client")));
    }

    [Fact]
    public async Task An_expired_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await RegisterWith(
            LocalTokens.Issue(TestApp.SigningKey, TestApp.ClientId, Guid.NewGuid(), "access", DateTime.UtcNow.AddMinutes(-10))));

    [Fact]
    public async Task A_token_from_the_configured_issuer_is_accepted() =>
        Assert.Equal(HttpStatusCode.Created, await RegisterWith(TestApp.Token(Guid.NewGuid())));
}

public sealed class HealthTests(Postgres postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task The_live_check_answers_without_a_token_and_without_the_database()
    {
        var reply = await StartHost().CreateClient().GetFromJsonAsync<JsonElement>("/health/live", Ct);

        Assert.Equal("ok", reply.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_ready_check_reaches_every_modules_database()
    {
        var reply = await StartHost().CreateClient().GetFromJsonAsync<JsonElement>("/health/ready", Ct);

        Assert.Equal("ready", reply.GetProperty("status").GetString());
    }
}
