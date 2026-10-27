namespace Timeline.Domain;

/// <summary>
/// A pool's pace, by additive increase and multiplicative decrease on the latency of its own Valkey
/// writes: once a second, a p99 above <see cref="Slow"/> halves the page rate; one at or below it adds
/// 10 percent; never below <paramref name="floor"/> or above <paramref name="ceiling"/>, the pool's
/// share of Valkey's writes. A burst of posts then makes fanout late, never makes Valkey slow for reads.
/// <paramref name="slowAbove"/> is <see cref="Slow"/> unless a deployment says otherwise
/// (Timeline:Fanout:SlowAbove): a shared test machine's Valkey answers slower than a cluster's.
/// </summary>
public sealed class PageRate(double floor, double ceiling, TimeSpan? slowAbove = null)
{
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(5);

    public TimeSpan SlowAbove { get; } = slowAbove ?? Slow;

    public double PagesPerSecond { get; private set; } = ceiling;

    public double Floor { get; } = floor;

    public double Ceiling { get; } = ceiling;

    /// <summary>The second's p99, or null when the pool wrote nothing (the rate stays).</summary>
    public double Adjust(TimeSpan? p99)
    {
        if (p99 is { } latency)
        {
            PagesPerSecond = latency > SlowAbove
                ? Math.Max(Floor, PagesPerSecond / 2)
                : Math.Min(Ceiling, PagesPerSecond * 1.1);
        }

        return PagesPerSecond;
    }
}
