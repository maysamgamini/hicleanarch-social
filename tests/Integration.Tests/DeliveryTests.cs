using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Social.BuildingBlocks.Infrastructure.Events;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>What 'posted' means, measured: stored, then fanned out, both shown to the author only.</summary>
public sealed class DeliveryTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<JsonElement?> Delivery(HttpClient client, Guid post)
    {
        var reply = await client.GetAsync($"/v1/posts/{post}/delivery", Ct);
        return reply.StatusCode == HttpStatusCode.OK ? await reply.Content.ReadFromJsonAsync<JsonElement>(Ct) : null;
    }

    /// <summary>Every timeline write fails: a program that dies between stored and fanned out.</summary>
    private sealed class Down : ITimelines
    {
        public Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken) => throw new IOException("Valkey is down");

        public Task<bool> Exists(Guid owner, CancellationToken cancellationToken) => throw new IOException("Valkey is down");

        public Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken) => throw new IOException("Valkey is down");

        public Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => throw new IOException("Valkey is down");

        public Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken) => throw new IOException("Valkey is down");

        public Task Touch(Guid owner, CancellationToken cancellationToken) => throw new IOException("Valkey is down");

        public Task Remove(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => throw new IOException("Valkey is down");
    }

    [Fact]
    public async Task A_post_is_stored_then_fanned_out_and_both_times_show_only_to_its_author()
    {
        var people = new People(StartHost());
        var (ada, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, ada);
        var created = await (await people.Publish(ada, "Measured", Guid.NewGuid().ToString())).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var post = created.GetProperty("post_id").GetGuid();

        JsonElement? delivery = null;
        await Eventually(async () => (delivery = await Delivery(people.As(ada), post)) is { } d && d.GetProperty("fanned_out_at").ValueKind != JsonValueKind.Null);

        Assert.Equal(created.GetProperty("created_at").GetDateTimeOffset(), delivery!.Value.GetProperty("stored_at").GetDateTimeOffset());
        Assert.True(delivery.Value.GetProperty("fanned_out_at").GetDateTimeOffset() >= delivery.Value.GetProperty("stored_at").GetDateTimeOffset());
        Assert.Equal(1, delivery.Value.GetProperty("followers_reached").GetInt64());
        var toOthers = await people.As(fan).GetAsync($"/v1/posts/{post}/delivery", Ct);
        Assert.Equal(HttpStatusCode.NotFound, toOthers.StatusCode);
        Assert.Equal("post_not_found", (await toOthers.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_crash_between_stored_and_fanned_out_still_ends_fanned_out_after_the_recovery_pass()
    {
        Guid ada, post;
        await using (var dying = TestApp.Start(Db, services => services
            .AddSingleton<ITimelines, Down>()
            .Configure<OutboxOptions>(o => o.RetryDelays = [TimeSpan.FromMilliseconds(10)])))
        {
            var people = new People(dying);
            ada = await people.SignUp();
            post = await people.Post(ada, "Interrupted");
            await Eventually(async () => await Delivery(people.As(ada), post) is { } d && d.GetProperty("fanned_out_at").ValueKind == JsonValueKind.Null);
        }

        var recovered = new People(StartHost()); // its first pass delivers every undelivered row

        await Eventually(async () => await Delivery(recovered.As(ada), post) is { } d && d.GetProperty("fanned_out_at").ValueKind != JsonValueKind.Null);
    }
}

/// <summary>Metrics are process-wide, so these run alone, after every other class.</summary>
[CollectionDefinition(nameof(Measured), DisableParallelization = true)]
public sealed class Measured;

[Collection(nameof(Measured))]
public sealed class AcknowledgmentMetricsTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static List<(string Name, double Value)> Listen(MeterListener listener, params string[] names)
    {
        var seen = new List<(string, double)>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (names.Contains(instrument.Name))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => { lock (seen) { seen.Add((i.Name, v)); } });
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => { lock (seen) { seen.Add((i.Name, v)); } });
        listener.Start();
        return seen;
    }

    [Fact]
    public async Task The_fanout_lag_histogram_records_one_value_per_post_even_when_its_event_arrives_twice()
    {
        using var listener = new MeterListener();
        var measured = Listen(listener, "social.fanout.lag", "social.posts.stored");
        var host = StartHost();
        var people = new People(host);
        var ada = await people.SignUp();
        var post = await people.Post(ada, "Counted once");
        await Eventually(() => Task.FromResult(measured.Any(m => m.Name == "social.fanout.lag")));

        var payload = await Db.Scalar<string>("SELECT payload::text FROM posts.outbox WHERE type = 'PostPublished'");
        var published = JsonSerializer.Deserialize<PostPublished>(payload, JsonSerializerOptions.Web)!;
        await host.Services.GetRequiredService<OutboxDispatcher>().Deliver(new OutboxRecord("Posts", published.EventId, nameof(PostPublished), payload), Ct);
        await Task.Delay(300, Ct);

        Assert.Single(measured, m => m.Name == "social.fanout.lag");
        Assert.Single(measured, m => m.Name == "social.posts.stored");
    }

    [Fact]
    public async Task A_post_is_counted_seen_the_first_time_a_readers_page_holds_it()
    {
        using var listener = new MeterListener();
        var measured = Listen(listener, "social.posts.seen");
        var people = new People(StartHost()); // the tests sample every read
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await people.Post(ada, "Seen once");

        await people.As(reader).GetAsync("/v1/feed", Ct);
        await people.As(reader).GetAsync("/v1/feed", Ct);

        Assert.Equal(1, measured.Where(m => m.Name == "social.posts.seen").Sum(m => m.Value));
    }
}
