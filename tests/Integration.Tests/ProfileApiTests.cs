using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests;

public sealed class ProfileApiTests(Postgres postgres) : IntegrationTest(postgres)
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _host = StartHost();
    }

    [Fact]
    public async Task Registering_without_a_token_is_401()
    {
        var reply = await TestApp.Client(_host).PutAsJsonAsync("/v1/me", new { handle = "ada", display_name = "Ada" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_user_registers_a_handle_and_anyone_can_read_the_profile()
    {
        var me = Guid.NewGuid();

        var registered = await TestApp.Client(_host, me).PutAsJsonAsync("/v1/me", new { handle = "Ada_L", display_name = "Ada Lovelace" }, Ct);
        var read = await TestApp.Client(_host).GetFromJsonAsync<JsonElement>("/v1/accounts/ada_l", Ct);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(me, read.GetProperty("account_id").GetGuid());
        Assert.Equal("Ada_L", read.GetProperty("handle").GetString());
        Assert.Equal("Ada Lovelace", read.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Sending_the_same_profile_again_answers_200()
    {
        var me = TestApp.Client(_host, Guid.NewGuid());
        await me.PutAsJsonAsync("/v1/me", new { handle = "grace", display_name = "Grace" }, Ct);

        var again = await me.PutAsJsonAsync("/v1/me", new { handle = "grace", display_name = "Grace" }, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_bad_handle_is_422_with_its_reason()
    {
        var reply = await TestApp.Client(_host, Guid.NewGuid()).PutAsJsonAsync("/v1/me", new { handle = "no spaces", display_name = "X" }, Ct);
        var problem = await reply.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.StatusCode);
        Assert.Equal("handle_characters", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_unknown_handle_is_404()
    {
        var reply = await TestApp.Client(_host).GetAsync("/v1/accounts/nobody_here", Ct);

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
    }
}
