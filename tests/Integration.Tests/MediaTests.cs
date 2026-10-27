using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests;

/// <summary>Images through the media store: the rules, on the fake store every rule test uses.</summary>
public sealed class MediaTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static async Task<JsonElement> Ticket(HttpClient client, long size)
    {
        var reply = await client.PostAsJsonAsync("/v1/media/upload-url", new { content_type = "image/png", size }, Ct);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        return await reply.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The client's own PUT to the upload URL, as a browser would send it.</summary>
    private static Task<HttpResponseMessage> Upload(WebApplicationFactory<Program> host, JsonElement ticket, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, ticket.GetProperty("upload_url").GetString()) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ticket.GetProperty("upload_headers").EnumerateObject().Single().Value.GetString()!);
        return host.CreateClient().SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, Guid media) => client.PostAsync($"/v1/media/{media}/confirm", null, Ct);

    private static async Task<Guid> Uploaded(WebApplicationFactory<Program> host, People people, Guid owner)
    {
        var ticket = await Ticket(people.As(owner), Png.Length);
        (await Upload(host, ticket, Png)).EnsureSuccessStatusCode();
        var media = ticket.GetProperty("media_id").GetGuid();
        (await Confirm(people.As(owner), media)).EnsureSuccessStatusCode();
        return media;
    }

    [Fact]
    public async Task An_upload_confirmed_twice_is_UPLOADED_once()
    {
        var host = StartHost();
        var people = new People(host);
        var ada = await people.SignUp();
        var ticket = await Ticket(people.As(ada), Png.Length);
        var media = ticket.GetProperty("media_id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(people.As(ada), media)).StatusCode); // nothing uploaded yet
        (await Upload(host, ticket, Png)).EnsureSuccessStatusCode();

        var replies = await Task.WhenAll(Confirm(people.As(ada), media), Confirm(people.As(ada), media));
        var first = await Db.Scalar<DateTimeOffset>("SELECT uploaded_at FROM posts.media WHERE id = @m", ("m", media));
        var again = await Confirm(people.As(ada), media);

        Assert.All(replies.Append(again), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("UPLOADED", await Db.Scalar<string>("SELECT status FROM posts.media WHERE id = @m", ("m", media)));
        Assert.Equal(first, await Db.Scalar<DateTimeOffset>("SELECT uploaded_at FROM posts.media WHERE id = @m", ("m", media)));
    }

    [Fact]
    public async Task An_upload_url_is_refused_after_it_expires()
    {
        var host = StartHost(settings: web => web.UseSetting("Media:UploadFor", "00:00:01"));
        var people = new People(host);
        var ada = await people.SignUp();
        var ticket = await Ticket(people.As(ada), Png.Length);

        await Task.Delay(TimeSpan.FromSeconds(2.2), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(host, ticket, Png)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(people.As(ada), ticket.GetProperty("media_id").GetGuid())).StatusCode);
    }

    [Fact]
    public async Task A_post_naming_someone_elses_media_is_422()
    {
        var host = StartHost();
        var people = new People(host);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        var bobs = await Uploaded(host, people, bob);

        var reply = await people.As(ada).PostAsJsonAsync("/v1/posts", new { text = "not mine", media_ids = new[] { bobs } }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.StatusCode);
        Assert.Equal("media_unusable", (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(0L, await Db.Scalar<long>("SELECT count(*) FROM posts.posts WHERE author_id = @a", ("a", ada)));
    }

    [Fact]
    public async Task A_post_with_media_is_hydrated_with_public_urls()
    {
        var host = StartHost();
        var people = new People(host);
        var (ada, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, ada);
        var (first, second) = (await Uploaded(host, people, ada), await Uploaded(host, people, ada));

        var reply = await people.As(ada).PostAsJsonAsync("/v1/posts", new { text = "two images", media_ids = new[] { second, first } }, Ct);
        reply.EnsureSuccessStatusCode();
        var post = (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("post_id").GetGuid();

        var item = (await people.As(fan).GetFromJsonAsync<JsonElement>("/v1/feed", Ct)).GetProperty("items")[0];
        Assert.Equal(post, item.GetProperty("post_id").GetGuid());
        var urls = item.GetProperty("media").EnumerateArray().Select(u => u.GetString()!).ToList();
        Assert.Equal([$"http://localhost/dev/media/media/{ada:N}/{second:N}", $"http://localhost/dev/media/media/{ada:N}/{first:N}"], urls);
        Assert.Equal(Png, await host.CreateClient().GetByteArrayAsync(urls[0], Ct)); // public: no token
    }

    [Fact]
    public async Task An_upload_never_put_in_a_post_is_deleted_after_a_day_and_one_in_a_post_is_kept()
    {
        var host = StartHost();
        var people = new People(host);
        var ada = await people.SignUp();
        var (unused, used) = (await Uploaded(host, people, ada), await Uploaded(host, people, ada));
        (await people.As(ada).PostAsJsonAsync("/v1/posts", new { text = "kept", media_ids = new[] { used } }, Ct)).EnsureSuccessStatusCode();
        await Db.Execute("UPDATE posts.media SET created_at = now() - interval '25 hours'");
        var unusedUrl = $"http://localhost/dev/media/media/{ada:N}/{unused:N}";

        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<Social.BuildingBlocks.Application.IDispatcher>()
                .Send<Posts.Application.CleanUpMedia, int>(new Posts.Application.CleanUpMedia(), Ct)).Value);
        }

        Assert.Equal(0L, await Db.Scalar<long>("SELECT count(*) FROM posts.media WHERE id = @m", ("m", unused)));
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.media WHERE id = @m", ("m", used)));
        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync(unusedUrl, Ct)).StatusCode);
    }
}
