using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using Timeline.Application;
using Timeline.Domain;

namespace Timeline.Infrastructure;

/// <summary>
/// Each pool's pace in this task. The Valkey adapter reports the latency of every page write made by a
/// pool's loop (the first reply of the page's pipelined batch: one round trip, whatever the page's
/// size); once a second each pool's p99 adjusts its <see cref="PageRate"/>; and every page waits its
/// turn at that rate. The ceiling caps the pool's share of Valkey writes: Timeline:Fanout:Ceiling:Small
/// and :Large, in pages a second.
/// </summary>
internal sealed class FanoutPace : BackgroundService, IFanoutPace
{
    /// <summary>The pool whose loop is running on this async flow, so the adapter knows whose write it timed.</summary>
    public static readonly AsyncLocal<FanoutPool?> CurrentPool = new();

    private static readonly Meter Meter = new("Social.Timeline.Pace");

    private readonly Dictionary<FanoutPool, Pool> _pools;

    public FanoutPace(IConfiguration configuration)
    {
        _pools = new()
        {
            [FanoutPool.Small] = new Pool(new PageRate(1, configuration.GetValue("Timeline:Fanout:Ceiling:Small", 50.0), SlowAbove(configuration))),
            [FanoutPool.Large] = new Pool(new PageRate(1, configuration.GetValue("Timeline:Fanout:Ceiling:Large", 20.0), SlowAbove(configuration))),
        };
        Meter.CreateObservableGauge("social.fanout.page_rate",
            () => _pools.Select(p => new Measurement<double>(p.Value.Rate.PagesPerSecond, new KeyValuePair<string, object?>("pool", p.Key.ToString()))),
            "{page}/s", "Each fanout pool's current page rate");
    }

    public double RateOf(FanoutPool pool) => _pools[pool].Rate.PagesPerSecond;

    /// <summary>Called by the Valkey adapter after each page write made by a pool's loop.</summary>
    public void Written(TimeSpan latency)
    {
        if (CurrentPool.Value is { } pool)
        {
            _pools[pool].Samples.Enqueue(latency);
        }
    }

    public async Task WaitTurn(FanoutPool pool, CancellationToken cancellationToken)
    {
        var state = _pools[pool];
        TimeSpan wait;
        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            var slot = state.NextSlot > now ? state.NextSlot : now;
            state.NextSlot = slot + TimeSpan.FromSeconds(1 / state.Rate.PagesPerSecond);
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var state in _pools.Values)
            {
                var second = new List<TimeSpan>();
                while (state.Samples.TryDequeue(out var sample))
                {
                    second.Add(sample);
                }

                lock (state)
                {
                    state.Rate.Adjust(second.Count == 0 ? null : P99(second));
                }
            }
        }
    }

    private static TimeSpan P99(List<TimeSpan> samples)
    {
        samples.Sort();
        return samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(0.99 * samples.Count) - 1)];
    }

    private static TimeSpan SlowAbove(IConfiguration configuration) => configuration.GetValue("Timeline:Fanout:SlowAbove", PageRate.Slow);

    private sealed class Pool(PageRate rate)
    {
        public PageRate Rate { get; } = rate;

        public ConcurrentQueue<TimeSpan> Samples { get; } = new();

        public DateTimeOffset NextSlot { get; set; } = DateTimeOffset.MinValue;
    }
}
