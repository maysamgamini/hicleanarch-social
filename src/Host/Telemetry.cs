using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Social.BuildingBlocks.Infrastructure.Events;

namespace Social.Host;

/// <summary>
/// Traces and metrics in the vendor-neutral OpenTelemetry format. On AWS a collector beside the
/// program receives them over OTLP and sends them to X-Ray and CloudWatch; locally nothing is exported.
/// Logs go to standard output, where the container's log driver ships them to CloudWatch Logs.
/// </summary>
internal static class Telemetry
{
    public const string ServiceName = "social-api";

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(OutboxDispatcher.ActivitySourceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Every meter of ours, by its prefix, without a line per meter to forget.
                .AddMeter("Social.*"));

        // The collector's address comes from the standard variable; without it, nothing is sent.
        if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }

    /// <summary>
    /// One JSON object per line, so CloudWatch Logs keeps each record as one event. Every record
    /// written inside a request carries that trace's TraceId and SpanId.
    /// </summary>
    public static ILoggingBuilder AddTraceCorrelatedConsole(this ILoggingBuilder logging)
    {
        logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        return logging.AddJsonConsole(options => options.IncludeScopes = true);
    }
}
