using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Notifications;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Events;

namespace Integration.Tests;

public sealed class NotificationTests(Postgres postgres) : IntegrationTest(postgres)
{
    private WebApplicationFactory<Program> _host = null!;
    private People _people = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _host = StartHost();
        _people = new People(_host);
    }

    private async Task<List<JsonElement>> Inbox(Guid account) =>
        [.. (await _people.As(account).GetFromJsonAsync<JsonElement>("/v1/notifications", Ct)).GetProperty("items").EnumerateArray()];

    /// <summary>Hands an event to every handler of the program, as the dispatcher does, as if Relationships had sent it.</summary>
    private Task Deliver(IntegrationEvent e) =>
        _host.Services.GetRequiredService<OutboxDispatcher>().Deliver(
            new OutboxRecord("Relationships", e.EventId, e.GetType().Name, JsonSerializer.Serialize(e, e.GetType(), JsonSerializerOptions.Web)), Ct);

    private Task<long> Notified(Guid recipient) =>
        Db.Scalar<long>("SELECT count(*) FROM notifications.notifications WHERE recipient_id = @r", ("r", recipient));

    [Fact]
    public async Task A_new_follower_is_announced_once_even_when_the_event_arrives_twice()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        var started = new FollowStarted(Guid.CreateVersion7(), DateTimeOffset.UtcNow, ada, bob, 1);

        await Deliver(started);
        await Deliver(started);

        var inbox = await Inbox(bob);
        var only = Assert.Single(inbox);
        Assert.Equal("new_follower", only.GetProperty("kind").GetString());
        Assert.Equal(ada, only.GetProperty("actor_id").GetGuid());
        Assert.Single(_host.Services.GetRequiredService<FakeNotificationSender>().Sent, s => s.RecipientId == bob);
    }

    [Fact]
    public async Task A_follow_through_the_api_reaches_the_followees_notifications()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());

        await _people.Follow(ada, bob);

        await Eventually(async () => await Notified(bob) == 1);
    }

    [Fact]
    public async Task A_FollowStarted_delivered_after_a_newer_block_creates_none()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());

        await Deliver(new AccountBlocked(Guid.CreateVersion7(), DateTimeOffset.UtcNow, bob, ada, 5));
        await Deliver(new FollowStarted(Guid.CreateVersion7(), DateTimeOffset.UtcNow, ada, bob, 4));

        Assert.Equal(0L, await Notified(bob));
    }

    [Fact]
    public async Task A_block_delivered_after_the_follow_removes_its_notification()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(ada, bob);
        await Eventually(async () => await Notified(bob) == 1);

        await _people.As(bob).PutAsync($"/v1/accounts/{ada}/block", null, Ct);

        await Eventually(async () => await Notified(bob) == 0);
    }

    [Fact]
    public async Task An_unblock_turns_the_copy_off_and_keeps_it()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await _people.As(bob).PutAsync($"/v1/accounts/{ada}/block", null, Ct);
        await _people.As(bob).DeleteAsync($"/v1/accounts/{ada}/block", Ct);

        await Eventually(async () => await Db.Scalar<bool>(
            "SELECT coalesce(bool_and(NOT \"on\"), false) FROM notifications.blocked_actors WHERE blocker_id = @b AND blocked_id = @a", ("b", bob), ("a", ada)));

        // A follow from before the block, delivered late, is still recognised as older than it.
        await Deliver(new FollowStarted(Guid.CreateVersion7(), DateTimeOffset.UtcNow, ada, bob, 1));
        Assert.Equal(0L, await Notified(bob));
    }

    [Fact]
    public async Task Read_up_to_marks_only_the_earlier_ones()
    {
        var bob = await _people.SignUp();
        var fans = await _people.SignUp(3);
        // Three different hours: an hour's new followers are one notification (s3-l13).
        var start = DateTimeOffset.UtcNow.AddHours(-3);
        for (var i = 0; i < fans.Length; i++)
        {
            await Deliver(new FollowStarted(Guid.CreateVersion7(), start.AddHours(i), fans[i], bob, 1));
        }

        var newestFirst = await Inbox(bob);
        var middle = newestFirst[1].GetProperty("notification_id").GetGuid();
        var marked = await (await _people.As(bob).PostAsJsonAsync("/v1/notifications/read-up-to", new { notification_id = middle }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var after = await Inbox(bob);

        Assert.Equal(2, marked.GetProperty("marked").GetInt32());
        Assert.Equal(JsonValueKind.Null, after[0].GetProperty("read_at").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, after[1].GetProperty("read_at").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, after[2].GetProperty("read_at").ValueKind);
    }

    [Fact]
    public async Task Notifications_come_newest_first_in_keyset_pages()
    {
        var bob = await _people.SignUp();
        var fans = await _people.SignUp(5);
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        for (var i = 0; i < fans.Length; i++)
        {
            await Deliver(new FollowStarted(Guid.CreateVersion7(), start.AddHours(i), fans[i], bob, 1));
        }

        var first = await _people.As(bob).GetFromJsonAsync<JsonElement>("/v1/notifications?limit=3", Ct);
        var before = first.GetProperty("next_before").GetGuid();
        var second = await _people.As(bob).GetFromJsonAsync<JsonElement>($"/v1/notifications?limit=3&before={before}", Ct);

        var actors = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("actor_id").GetGuid());
        Assert.Equal(fans.Reverse(), actors);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_before").ValueKind);
    }

    /// <summary>The start of the hour before this one: a whole hour, over, whatever the time is now.</summary>
    private static DateTimeOffset LastHour()
    {
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
    }

    private async Task DeliverFollows(Guid followee, int count, DateTimeOffset hour)
    {
        foreach (var batch in Enumerable.Range(0, count).Chunk(20))
        {
            await Task.WhenAll(batch.Select(i => Deliver(new FollowStarted(Guid.CreateVersion7(), hour.AddSeconds(i * 3), Guid.CreateVersion7(), followee, 1))));
        }
    }

    [Fact]
    public async Task A_thousand_follows_of_one_account_in_an_hour_make_one_notification_row_with_count_1000()
    {
        var bob = await _people.SignUp();
        var hour = LastHour();

        await DeliverFollows(bob, 1_000, hour);

        var only = Assert.Single(await Inbox(bob));
        Assert.Equal(1_000, only.GetProperty("count").GetInt32());
        Assert.Equal(3, only.GetProperty("actors").GetArrayLength());
        Assert.Equal(1L, await Notified(bob));
    }

    [Fact]
    public async Task A_duplicate_event_does_not_double_the_count()
    {
        var bob = await _people.SignUp();
        var hour = LastHour();
        var first = new FollowStarted(Guid.CreateVersion7(), hour, Guid.CreateVersion7(), bob, 1);
        var second = new FollowStarted(Guid.CreateVersion7(), hour.AddMinutes(1), Guid.CreateVersion7(), bob, 1);

        await Deliver(first);
        await Deliver(second);
        await Deliver(second);
        await Deliver(first);

        Assert.Equal(2, Assert.Single(await Inbox(bob)).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_celebritys_new_followers_make_one_summary_an_hour_and_no_message_per_follow()
    {
        var star = await _people.SignUp();
        var sent = _host.Services.GetRequiredService<FakeNotificationSender>().Sent;
        await Deliver(new FollowerCountChanged(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(-2), star, 30_000));
        var hour = LastHour();

        await DeliverFollows(star, 50, hour);
        Assert.DoesNotContain(sent, s => s.RecipientId == star);

        var summaries = _host.Services.GetRequiredService<HourlySummaries>();
        await summaries.SendDue(Ct);
        await summaries.SendDue(Ct); // a second pass sends nothing again

        var summary = Assert.Single(sent, s => s.RecipientId == star);
        Assert.Equal(50, summary.Count);
    }
}
