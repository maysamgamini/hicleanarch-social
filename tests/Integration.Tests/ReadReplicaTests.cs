using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Social.BuildingBlocks.Application;

namespace Integration.Tests;

/// <summary>Read replicas for hydration: the feed's reads of posts and profiles go to the reader endpoint.</summary>
public sealed class ReadReplicaTests(Postgres postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task The_hydration_queries_run_on_the_reader_connection()
    {
        // The reader endpoint, here the same database, recognisable by its Application Name.
        var people = new People(StartHost(settings: web => web
            .UseSetting("ConnectionStrings:PostsRead", Db.For("Posts") + ";Application Name=posts-reader")
            .UseSetting("ConnectionStrings:AccountsRead", Db.For("Accounts") + ";Application Name=accounts-reader")));
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await people.As(reader).GetAsync("/v1/feed", Ct);
        await TimelineBuilt(reader);
        var post = await people.Post(ada, "hydrated from the reader");
        await Delivered();

        var page = await people.As(reader).GetFromJsonAsync<JsonElement>("/v1/feed", Ct);

        Assert.Equal(post, page.GetProperty("items")[0].GetProperty("post_id").GetGuid());
        Assert.True(await Db.Scalar<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND application_name = 'posts-reader' AND query LIKE '%posts.posts%')
            """), "No posts query ran on the reader connection.");
        Assert.True(await Db.Scalar<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND application_name = 'accounts-reader' AND query LIKE '%accounts%')
            """), "No profiles query ran on the reader connection.");
    }

    [Fact]
    public async Task The_authors_own_get_right_after_a_publish_reads_the_writer()
    {
        // A reader endpoint that has not caught up at all: another database, with no posts.
        var behind = await Server.NewDatabase();
        var host = StartHost(settings: web => web.UseSetting("ConnectionStrings:PostsRead", behind.For("Posts")));
        var people = new People(host);
        var ada = await people.SignUp();

        var post = await people.Post(ada, "my own, at once");
        var own = await people.As(ada).GetAsync($"/v1/posts/{post}", Ct);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        await using var scope = host.Services.CreateAsyncScope();
        var hydrated = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query<GetPosts, IReadOnlyList<PostSummary>>(new GetPosts([post]), Ct);
        Assert.Empty(hydrated.Value); // the feed's read goes to the reader, which is behind
    }
}
