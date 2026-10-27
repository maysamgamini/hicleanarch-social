using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Social.BuildingBlocks.Infrastructure.Events;

namespace Social.BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// One image, two roles (Host:Role). A web task serves requests and runs no background work; a worker
/// task runs every background service (event delivery, jobs, recovery passes) and serves no request
/// but its health checks. All, the default, is both, for development and tests.
/// </summary>
public enum HostRole
{
    All,
    Web,
    Worker,
}

public static class HostRoles
{
    public static HostRole Read(IConfiguration configuration) =>
        Enum.TryParse<HostRole>(configuration["Host:Role"] ?? nameof(HostRole.All), ignoreCase: true, out var role)
            ? role
            : throw new InvalidOperationException($"Host:Role {configuration["Host:Role"]} is not Web, Worker or All.");

    public static bool ServesRequests(this HostRole role) => role != HostRole.Worker;

    /// <summary>
    /// Applied after every module has registered its services. A web task keeps no hosted service but
    /// the graceful stop, and its bus holds no committed event: rows wait in the outboxes for a worker,
    /// which polls them every second.
    /// </summary>
    public static IServiceCollection ApplyRole(this IServiceCollection services, HostRole role)
    {
        switch (role)
        {
            case HostRole.Web:
                services.RemoveAll<IHostedService>();
                services.AddHostedService<GracefulStop>();
                services.Replace(ServiceDescriptor.Singleton(new EventBus(holdsCommittedEvents: false)));
                break;
            case HostRole.Worker:
                services.Configure<OutboxOptions>(o => o.PollCommitted = true);
                break;
        }

        return services;
    }
}
