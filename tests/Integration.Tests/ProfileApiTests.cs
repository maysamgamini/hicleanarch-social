using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests;

public sealed class ProfileApiTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = TestHost.Start();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Registering_without_a_token_is_401()
    {
        var reply = await TestHost.Client(_host).PutAsJsonAsync("/v1/me", new { handle = "ada", display_name = "Ada" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_user_registers_a_handle_and_anyone_can_read_the_profile()
    {
        var me = Guid.NewGuid();

        var registered = await TestHost.Client(_host, me).PutAsJsonAsync("/v1/me", new { handle = "Ada_L", display_name = "Ada Lovelace" }, Ct);
        var read = await TestHost.Client(_host).GetFromJsonAsync<JsonElement>("/v1/accounts/ada_l", Ct);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(me, read.GetProperty("account_id").GetGuid());
        Assert.Equal("Ada_L", read.GetProperty("handle").GetString());
        Assert.Equal("Ada Lovelace", read.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Sending_the_same_profile_again_answers_200()
    {
        var me = TestHost.Client(_host, Guid.NewGuid());
        await me.PutAsJsonAsync("/v1/me", new { handle = "grace", display_name = "Grace" }, Ct);

        var again = await me.PutAsJsonAsync("/v1/me", new { handle = "grace", display_name = "Grace" }, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_bad_handle_is_422_with_its_reason()
    {
        var reply = await TestHost.Client(_host, Guid.NewGuid()).PutAsJsonAsync("/v1/me", new { handle = "no spaces", display_name = "X" }, Ct);
        var problem = await reply.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.StatusCode);
        Assert.Equal("handle_characters", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_unknown_handle_is_404()
    {
        var reply = await TestHost.Client(_host).GetAsync("/v1/accounts/nobody_here", Ct);

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
    }
}
