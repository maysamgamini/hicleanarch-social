using System.Collections.Concurrent;
using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Events;

namespace Integration.Tests;

/// <summary>One image, two roles: web tasks serve and run no background work; worker tasks work and serve only their health.</summary>
public sealed class RoleTests(Postgres postgres) : IntegrationTest(postgres)
{
    /// <summary>Each FollowStarted that reached this handler's own code, and how many finished.</summary>
    private static readonly ConcurrentDictionary<Guid, int> Started = new();
    private static readonly ConcurrentDictionary<Guid, int> Finished = new();

    private sealed class SlowHandler : IIntegrationEventHandler<FollowStarted>
    {
        public async Task Handle(FollowStarted e, CancellationToken ct)
        {
            Started.AddOrUpdate(e.FollowerId, 1, (_, n) => n + 1);
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            Finished.AddOrUpdate(e.FollowerId, 1, (_, n) => n + 1);
        }
    }

    private WebApplicationFactory<Program> Start(string role, bool slow = false) =>
        StartHost(services =>
        {
            if (slow)
            {
                services.AddEventHandler<FollowStarted, SlowHandler>("Notifications");
            }
        }, web => web.UseSetting("Host:Role", role));

    private Task<long> Undelivered() => Db.Scalar<long>("SELECT count(*) FROM relationships.outbox WHERE delivered_at IS NULL");

    [Fact]
    public async Task A_web_task_runs_no_handler_and_a_worker_picks_its_events_up()
    {
        var web = new People(Start("Web"));
        var (ada, bob) = (await web.SignUp(), await web.SignUp());

        (await web.Follow(ada, bob)).EnsureSuccessStatusCode();
        await Task.Delay(1500, Ct);

        Assert.Equal(1L, await Undelivered());
        Assert.Equal(0L, await Db.Scalar<long>("SELECT count(*) FROM notifications.notifications"));

        _ = Start("Worker").Services;

        await Eventually(async () => await Undelivered() == 0);
        await Eventually(async () => await Db.Scalar<long>("SELECT count(*) FROM notifications.notifications WHERE recipient_id = @b", ("b", bob)) == 1);
    }

    [Fact]
    public async Task A_worker_task_maps_no_public_endpoint_but_its_health()
    {
        var worker = Start("Worker").CreateClient();
        worker.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApp.Token(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, (await worker.GetAsync("/v1/feed", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await worker.PutAsync($"/v1/accounts/{Guid.NewGuid()}/follow", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/health/ready", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_stopping_worker_finishes_the_work_in_hand()
    {
        var web = new People(Start("Web"));
        var (ada, bob) = (await web.SignUp(), await web.SignUp());
        await using (var worker = TestApp.Start(Db, s => s.AddEventHandler<FollowStarted, SlowHandler>("Notifications"), w => w.UseSetting("Host:Role", "Worker")))
        {
            _ = worker.Services;
            (await web.Follow(ada, bob)).EnsureSuccessStatusCode();
            await Eventually(() => Task.FromResult(Started.ContainsKey(ada)));
        }

        // The stop waited for the delivery in hand: the handler finished, and its row is marked delivered.
        Assert.Equal(1, Finished.GetValueOrDefault(ada));
        Assert.Equal(0L, await Db.Scalar<long>("SELECT count(*) FROM relationships.outbox WHERE delivered_at IS NULL AND type = 'FollowStarted'"));
    }
}
