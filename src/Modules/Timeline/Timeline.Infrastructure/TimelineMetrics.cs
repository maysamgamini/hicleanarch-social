using System.Diagnostics.Metrics;

using Microsoft.Extensions.Configuration;

using StackExchange.Redis;

using Timeline.Application;

namespace Timeline.Infrastructure;

/// <summary>Timeline's acknowledgments as numbers: how long fanout takes, and how many posts are seen.</summary>
internal static class TimelineMetrics
{
    public const string MeterName = "Social.Timeline";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Fanned out: from the post's commit (its PostPublished's created_at) to the last timeline write. One value per post.</summary>
    public static readonly Histogram<double> FanoutLag = Meter.CreateHistogram<double>("social.fanout.lag", "s",
        "From a post's commit to its last timeline write");

    /// <summary>Seen: the first time a reader's page held a post, counted on a sample of reads.</summary>
    public static readonly Counter<long> Seen = Meter.CreateCounter<long>("social.posts.seen", "{post}",
        "First hydrations of a post per reader, sampled");
}

/// <summary>
/// Counts a post as seen the first time a sampled reader's page holds it: one SET NX per post in
/// Valkey (seen:{post}:{reader}, kept 7 days, as long as a timeline), so a re-read never counts again.
/// </summary>
internal sealed class SampledSeenPosts(IConnectionMultiplexer valkey, IConfiguration configuration) : ISeenPosts
{
    private readonly double _rate = configuration.GetValue("Timeline:SeenSampleRate", 0.01);

    public async Task Saw(Guid reader, IReadOnlyList<PostCard> posts, CancellationToken cancellationToken)
    {
        var sampled = posts.Where(_ => Random.Shared.NextDouble() < _rate).ToList();
        if (sampled.Count == 0)
        {
            return;
        }

        var batch = valkey.GetDatabase().CreateBatch();
        var firsts = sampled.Select(p => batch.StringSetAsync($"seen:{p.PostId:N}:{reader:N}", 1, TimeSpan.FromDays(7), When.NotExists)).ToList();
        batch.Execute();
        var first = (await Task.WhenAll(firsts).WaitAsync(cancellationToken)).Count(f => f);
        if (first > 0)
        {
            TimelineMetrics.Seen.Add(first);
        }
    }
}
