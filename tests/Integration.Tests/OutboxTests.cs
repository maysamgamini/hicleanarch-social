using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Accounts.Contracts;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Relationships.Contracts;
using Relationships.Infrastructure.Persistence;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Events;

namespace Integration.Tests;

/// <summary>Events in process: written with the change, handed out after the commit, handled once, recovered by the next pass.</summary>
public sealed class OutboxTests(Postgres postgres) : IntegrationTest(postgres)
{
    private const string Relationships = "Relationships";

    /// <summary>Every event that reached a test handler's own code, past the inbox, by event id.</summary>
    private static readonly ConcurrentDictionary<Guid, ConcurrentBag<IntegrationEvent>> Handled = new();

    public sealed record Ping(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record SlowPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    private sealed class Recorder :
        IIntegrationEventHandler<Ping>, IIntegrationEventHandler<FollowStarted>, IIntegrationEventHandler<FollowEnded>,
        IIntegrationEventHandler<AccountBlocked>, IIntegrationEventHandler<AccountRegistered>, IIntegrationEventHandler<AccountDeactivated>
    {
        public Task Handle(Ping e, CancellationToken ct) => Record(e);

        public Task Handle(FollowStarted e, CancellationToken ct) => Record(e);

        public Task Handle(FollowEnded e, CancellationToken ct) => Record(e);

        public Task Handle(AccountBlocked e, CancellationToken ct) => Record(e);

        public Task Handle(AccountRegistered e, CancellationToken ct) => Record(e);

        public Task Handle(AccountDeactivated e, CancellationToken ct) => Record(e);

        private static Task Record(IntegrationEvent e)
        {
            Handled.GetOrAdd(e.EventId, _ => []).Add(e);
            return Task.CompletedTask;
        }
    }

    /// <summary>Takes five seconds, longer than any reply may wait.</summary>
    private sealed class Slow : IIntegrationEventHandler<FollowStarted>
    {
        public async Task Handle(FollowStarted e, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            Handled.GetOrAdd(e.EventId, _ => []).Add(e);
        }
    }

    private WebApplicationFactory<Program> Host(bool slow = false) => StartHost(services =>
    {
        services.AddEventHandler<Ping, Recorder>(Relationships);
        services.AddEventHandler<AccountRegistered, Recorder>(Relationships);
        services.AddEventHandler<AccountDeactivated, Recorder>(Relationships);
        services.AddEventHandler<FollowEnded, Recorder>(Relationships);
        services.AddEventHandler<AccountBlocked, Recorder>(Relationships);
        if (slow)
        {
            services.AddEventHandler<FollowStarted, Slow>(Relationships);
        }
        else
        {
            services.AddEventHandler<FollowStarted, Recorder>(Relationships);
        }
    });

    private static IEnumerable<T> Seen<T>(Func<T, bool> which) => Handled.Values.SelectMany(b => b).OfType<T>().Where(which);

    private Task<bool> Delivered(Guid eventId) =>
        Db.Scalar<bool>("SELECT count(*) = 1 FROM relationships.outbox WHERE id = @id AND delivered_at IS NOT NULL", ("id", eventId));

    /// <summary>Writes an event the way a use case does: in a transaction of the module, then commit or roll back.</summary>
    private static async Task Write(WebApplicationFactory<Program> host, IntegrationEvent e, bool commit)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelationshipsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetOutbox(Relationships).Add(e, Ct);
        if (commit)
        {
            await transaction.CommitAsync(Ct);
        }
        else
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    [Fact]
    public async Task A_rolled_back_change_dispatches_nothing()
    {
        var host = Host();
        var ping = new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Write(host, ping, commit: false);
        await Task.Delay(500, Ct);

        Assert.False(Handled.ContainsKey(ping.EventId));
        Assert.Equal(0L, await Db.Scalar<long>("SELECT count(*) FROM relationships.outbox WHERE id = @id", ("id", ping.EventId)));
    }

    [Fact]
    public async Task A_committed_follow_reaches_a_handler_once_with_its_pair_version()
    {
        var host = Host();
        var people = new People(host);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());

        (await people.Follow(ada, bob)).EnsureSuccessStatusCode();
        await Eventually(() => Task.FromResult(Seen<FollowStarted>(e => e.FollowerId == ada).Any()));
        var started = Seen<FollowStarted>(e => e.FollowerId == ada).Single();
        await Eventually(() => Delivered(started.EventId));

        // Delivered a second time, as after a crash between handling and marking: the inbox drops it.
        var record = new OutboxRecord(Relationships, started.EventId, nameof(FollowStarted), JsonSerializer.Serialize(started, JsonSerializerOptions.Web));
        await host.Services.GetRequiredService<OutboxDispatcher>().Deliver(record, Ct);

        Assert.Single(Handled[started.EventId]);
        Assert.Equal(bob, started.FolloweeId);
        Assert.Equal(1L, started.PairVersion);
        Assert.Contains(Seen<AccountRegistered>(e => e.AccountId == ada), e => e.Handle.StartsWith('u'));
    }

    [Fact]
    public async Task A_row_committed_by_a_program_that_then_crashed_is_delivered_by_the_next_pass()
    {
        // A row left behind by a program that stopped right after its commit, before dispatching.
        var ping = new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Db.Execute("INSERT INTO relationships.outbox (id, type, version, occurred_at, payload, attempts) VALUES (@id, 'Ping', 1, now(), @payload::jsonb, 0)",
            ("id", ping.EventId), ("payload", JsonSerializer.Serialize(ping, JsonSerializerOptions.Web)));

        _ = Host().Services; // starting a program is what recovers: its first pass takes every undelivered row

        await Eventually(() => Delivered(ping.EventId));
        Assert.Single(Handled[ping.EventId]);
    }

    [Fact]
    public async Task A_slow_handler_does_not_delay_the_reply()
    {
        var host = Host(slow: true);
        var people = new People(host);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());

        var clock = Stopwatch.StartNew();
        var reply = await people.Follow(ada, bob);
        var replied = clock.Elapsed;

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.True(replied < TimeSpan.FromSeconds(3), $"The reply took {replied}; the handler takes 5 s.");
        await Eventually(() => Task.FromResult(Seen<FollowStarted>(e => e.FollowerId == ada).Any()), seconds: 20);
    }

    [Fact]
    public async Task A_block_announces_itself_and_a_FollowEnded_for_each_follow_it_removed_with_its_version()
    {
        var host = Host();
        var people = new People(host);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        await people.Follow(ada, bob);
        await people.Follow(bob, ada);

        (await people.As(ada).PutAsync($"/v1/accounts/{bob}/block", null, Ct)).EnsureSuccessStatusCode();

        await Eventually(() => Task.FromResult(Seen<AccountBlocked>(e => e.BlockerId == ada).Any() && Seen<FollowEnded>(e => e.FollowerId == ada || e.FollowerId == bob).Count() == 2));
        var blocked = Seen<AccountBlocked>(e => e.BlockerId == ada).Single();
        Assert.Equal(3L, blocked.PairVersion);
        Assert.All(Seen<FollowEnded>(e => e.FollowerId == ada || e.FollowerId == bob), e => Assert.Equal(3L, e.PairVersion));
    }

    [Fact]
    public async Task Deactivating_an_account_announces_it_once()
    {
        var host = Host();
        var people = new People(host);
        var ada = await people.SignUp();

        var first = await people.As(ada).PostAsync("/v1/me/deactivate", null, Ct);
        var second = await people.As(ada).PostAsync("/v1/me/deactivate", null, Ct);

        Assert.Equal("DEACTIVATED", (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await Eventually(() => Task.FromResult(Seen<AccountDeactivated>(e => e.AccountId == ada).Any()));
        await Task.Delay(300, Ct);
        Assert.Single(Seen<AccountDeactivated>(e => e.AccountId == ada));
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM accounts.outbox WHERE type = 'AccountDeactivated'"));
    }

    [Fact]
    public async Task The_age_of_the_oldest_undelivered_event_is_a_metric()
    {
        var gauges = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OutboxDispatcher.MeterName)
            {
                gauges.Add(instrument.Name);
            }
        };
        listener.Start();

        _ = Host().Services;

        await Eventually(() => Task.FromResult(gauges.Contains("outbox.oldest_undelivered_age")));
    }
}
