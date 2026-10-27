using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Relationships.Contracts;

using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Notifications;

/// <summary>
/// Notifications is one project, not four: it has no rules of its own to protect with layers. It only
/// reacts to events, keeps what it was told, and sends.
/// </summary>
public static class NotificationsModule
{
    public const string Name = "Notifications";

    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Notifications")
            ?? throw new InvalidOperationException("ConnectionStrings:Notifications is not set.");
        services.AddDbContext<NotificationsDbContext>((sp, db) => NotificationsDbOptions
            .Configure((DbContextOptionsBuilder<NotificationsDbContext>)db, connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor<NotificationsDbContext>>()));
        // It publishes nothing; the outbox table exists because the vendored module events map both.
        services.AddModuleEvents<NotificationsDbContext>(Name);
        services.AddEventHandler<FollowStarted, NotificationHandlers>(Name);
        services.AddEventHandler<AccountBlocked, NotificationHandlers>(Name);
        services.AddEventHandler<AccountUnblocked, NotificationHandlers>(Name);
        services.AddSingleton<IModuleMigrator, NotificationsMigrator>();
        services.AddSingleton(new ModuleDatabase(Name, NotificationsDbContext.Schema, connectionString));
        services.AddSingleton<FakeNotificationSender>();
        services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<FakeNotificationSender>());
        services.AddHostedService<BlockedActorsPurge>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/v1/notifications").WithTags("Notifications");

        // Newest first, a page at a time: the next page starts before the last id of this one.
        notifications.MapGet("/", async (Guid? before, int? limit, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var page = await db.Read($"""
                SELECT id, kind, actor_id, subject_id, created_at, read_at FROM notifications.notifications
                WHERE recipient_id = @me {(before is null ? "" : "AND id < @before")}
                ORDER BY id DESC LIMIT @take
                """,
                r => new NotificationReply(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                    r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)),
                ct, [("me", user.UserId()), ("take", take + 1), .. before is { } b ? new[] { ("before", (object)b) } : []]);
            var items = page.Take(take).ToList();
            return Results.Ok(new NotificationsPage(items, page.Count > take ? items[^1].NotificationId : null));
        });

        // Marks every notification of the caller up to and including this one as read.
        notifications.MapPost("/read-up-to", async (ReadUpTo request, ClaimsPrincipal user, NotificationsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var marked = await db.Execute("""
                UPDATE notifications.notifications SET read_at = @now
                WHERE recipient_id = @me AND read_at IS NULL AND id <= @upTo
                """, ct, ("now", clock.GetUtcNow()), ("me", user.UserId()), ("upTo", request.NotificationId));
            return Results.Ok(new { marked });
        });

        return app;
    }
}

internal sealed record NotificationReply(Guid NotificationId, string Kind, Guid ActorId, Guid? SubjectId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

internal sealed record NotificationsPage(IReadOnlyList<NotificationReply> Items, Guid? NextBefore);

internal sealed record ReadUpTo(Guid NotificationId);

/// <summary>Off copies of blocks are kept 30 days, longer than any redelivery, then deleted. Hourly.</summary>
internal sealed class BlockedActorsPurge(IServiceScopeFactory scopes, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    public async Task<int> PurgeOnce(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var cutoff = clock.GetUtcNow() - Keep;
        return await db.BlockedActors.Where(b => !b.On && b.ChangedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PurgeOnce(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException and not ObjectDisposedException)
            {
                // the next hour tries again
            }
        }
    }
}
