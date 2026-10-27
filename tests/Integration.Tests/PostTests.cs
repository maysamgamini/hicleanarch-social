using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Events;

namespace Integration.Tests;

public sealed class PostTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static readonly ConcurrentDictionary<Guid, int> Published = new();

    private sealed class Recorder : IIntegrationEventHandler<PostPublished>
    {
        public Task Handle(PostPublished e, CancellationToken ct)
        {
            Published.AddOrUpdate(e.PostId, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>Fails every time: with it, a program dispatches nothing, as one that died after the commit.</summary>
    private sealed class Broken : IIntegrationEventHandler<PostPublished>
    {
        public Task Handle(PostPublished e, CancellationToken ct) => throw new InvalidOperationException("the program died here");
    }

    private WebApplicationFactory<Program> Host(bool broken = false) => StartHost(services =>
    {
        if (broken)
        {
            services.AddEventHandler<PostPublished, Broken>("Posts");
            services.Configure<OutboxOptions>(o => o.RetryDelays = [TimeSpan.FromMilliseconds(10)]);
        }
        else
        {
            services.AddEventHandler<PostPublished, Recorder>("Posts");
        }
    });

    private static async Task<JsonElement> Json(HttpResponseMessage reply) => await reply.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_post_is_201_stored_with_its_location()
    {
        var people = new People(Host());
        var ada = await people.SignUp();

        var reply = await people.Publish(ada, "Hello, world", "k-1");
        var body = await Json(reply);

        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        Assert.Equal("stored", body.GetProperty("delivery").GetString());
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal($"/v1/posts/{body.GetProperty("post_id").GetGuid()}", reply.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_same_key_twice_gives_one_post_and_the_same_reply()
    {
        var people = new People(Host());
        var ada = await people.SignUp();

        var first = await people.Publish(ada, "Only once", "same-key");
        var second = await people.Publish(ada, "Only once", "same-key");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.posts WHERE author_id = @a", ("a", ada)));
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.outbox WHERE type = 'PostPublished'"));
    }

    [Fact]
    public async Task The_same_key_with_another_body_is_422_and_no_key_is_400()
    {
        var people = new People(Host());
        var ada = await people.SignUp();
        await people.Publish(ada, "First text", "reused");

        var reused = await people.Publish(ada, "Another text", "reused");
        var missing = await people.Publish(ada, "No key", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", (await Json(reused)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("idempotency_key_required", (await Json(missing)).GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Text_outside_1_to_500_characters_is_422(string text)
    {
        var people = new People(Host());
        var ada = await people.SignUp();

        var empty = await people.Publish(ada, text, Guid.NewGuid().ToString());
        var tooLong = await people.Publish(ada, new string('x', 501), Guid.NewGuid().ToString());

        Assert.Equal("text_length", (await Json(empty)).GetProperty("reason").GetString());
        Assert.Equal("text_length", (await Json(tooLong)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Created, (await people.Publish(ada, new string('x', 500), Guid.NewGuid().ToString())).StatusCode);
    }

    [Fact]
    public async Task A_crash_after_the_commit_still_publishes_PostPublished()
    {
        Guid post;
        await using (var dying = TestApp.Start(Db, services =>
        {
            services.AddEventHandler<PostPublished, Broken>("Posts");
            services.Configure<OutboxOptions>(o => o.RetryDelays = [TimeSpan.FromMilliseconds(10)]);
        }))
        {
            var people = new People(dying);
            post = await people.Post(await people.SignUp(), "Survives the crash");
            await Task.Delay(300, Ct);
        }

        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.outbox WHERE type = 'PostPublished' AND delivered_at IS NULL"));

        _ = Host().Services; // the next program's first pass sends every undelivered row

        await Eventually(() => Task.FromResult(Published.ContainsKey(post)));
        Assert.Equal(1, Published[post]);
    }

    [Fact]
    public async Task A_deleted_post_is_404_to_others_and_its_tombstone_to_the_author()
    {
        var people = new People(Host());
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        var post = await people.Post(ada, "Soon gone");

        var deleted = await people.As(ada).DeleteAsync($"/v1/posts/{post}", Ct);
        var again = await people.As(ada).DeleteAsync($"/v1/posts/{post}", Ct);
        var toAuthor = await people.As(ada).GetAsync($"/v1/posts/{post}", Ct);
        var toOthers = await people.As(bob).GetAsync($"/v1/posts/{post}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var tombstone = await Json(toAuthor);
        Assert.Equal("DELETED", tombstone.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("text").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, toOthers.StatusCode);
        Assert.Equal("post_not_found", (await Json(toOthers)).GetProperty("reason").GetString());
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.outbox WHERE type = 'PostDeleted'"));
    }

    [Fact]
    public async Task Only_the_author_edits_and_the_edit_is_announced()
    {
        var people = new People(Host());
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        var post = await people.Post(ada, "Frist");

        var byOther = await people.As(bob).PatchAsJsonAsync($"/v1/posts/{post}", new { text = "Hijacked" }, Ct);
        var byAuthor = await people.As(ada).PatchAsJsonAsync($"/v1/posts/{post}", new { text = "First" }, Ct);

        var edited = await Json(byAuthor);
        Assert.Equal(HttpStatusCode.NotFound, byOther.StatusCode);
        Assert.Equal("First", edited.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("edited_at").ValueKind);
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.outbox WHERE type = 'PostEdited'"));
    }

    [Fact]
    public async Task Ids_sort_by_creation_time()
    {
        var people = new People(Host());
        var ada = await people.SignUp();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await people.Post(ada, $"Post {i}"));
            await Task.Delay(5, Ct); // a new millisecond each: within one, UUIDv7 promises no order
        }

        var read = new List<DateTimeOffset>();
        foreach (var id in ids)
        {
            read.Add((await people.As(ada).GetFromJsonAsync<JsonElement>($"/v1/posts/{id}", Ct)).GetProperty("created_at").GetDateTimeOffset());
        }

        Assert.Equal(ids.OrderBy(i => i.ToString("N"), StringComparer.Ordinal), ids);
        Assert.Equal(read.Order(), read);
        Assert.All(ids.Zip(read), p => Assert.Equal(p.Second.ToUnixTimeMilliseconds(), UuidV7Milliseconds(p.First)));
    }

    [Fact]
    public async Task Recent_posts_by_authors_come_newest_first_and_skip_deleted_ones()
    {
        var host = Host();
        var people = new People(host);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        var a1 = await people.Post(ada, "a1");
        await Task.Delay(5, Ct);
        var b1 = await people.Post(bob, "b1");
        await Task.Delay(5, Ct);
        var a2 = await people.Post(ada, "a2");
        await people.As(ada).DeleteAsync($"/v1/posts/{a2}", Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var recent = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query<GetRecentByAuthors, IReadOnlyList<PostSummary>>(new GetRecentByAuthors([ada, bob], 10), Ct);

        Assert.Equal([b1, a1], recent.Value.Select(p => p.PostId));
    }

    private static long UuidV7Milliseconds(Guid id) => Convert.ToInt64(id.ToString("N")[..12], 16);
}
