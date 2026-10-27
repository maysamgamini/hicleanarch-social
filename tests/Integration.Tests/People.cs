using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Integration.Tests;

/// <summary>Signed-up people of a test, each with a client signed in as them.</summary>
public sealed class People(WebApplicationFactory<Program> host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new account with a handle of its own, registered through the API.</summary>
    public async Task<Guid> SignUp(string? handle = null)
    {
        var id = Guid.NewGuid();
        handle ??= "u" + id.ToString("N")[..12];
        var reply = await As(id).PutAsJsonAsync("/v1/me", new { handle, display_name = handle }, Ct);
        reply.EnsureSuccessStatusCode();
        return id;
    }

    public async Task<Guid[]> SignUp(int count)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await SignUp();
        }

        return ids;
    }

    public HttpClient As(Guid id) => TestApp.Client(host, id);

    public Task<HttpResponseMessage> Follow(Guid follower, Guid followee) => As(follower).PutAsync($"/v1/accounts/{followee}/follow", null, Ct);

    public Task<HttpResponseMessage> Unfollow(Guid follower, Guid followee) => As(follower).DeleteAsync($"/v1/accounts/{followee}/follow", Ct);
}
