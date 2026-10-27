using System.Globalization;

namespace GraphGen;

internal sealed record Options(string Connection, int Accounts, double MeanFollowing, double Exponent, int Seed, int Readers, int TimeoutMs)
{
    public static Options Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        var connection = Value("connection", Environment.GetEnvironmentVariable("GRAPHGEN_CONNECTION") ?? "");
        if (connection.Length == 0)
        {
            throw new ArgumentException("Pass --connection (the owner's connection string) or set GRAPHGEN_CONNECTION.");
        }

        var c = CultureInfo.InvariantCulture;
        return new Options(connection,
            int.Parse(Value("accounts", "100000"), c), double.Parse(Value("mean-following", "30"), c), double.Parse(Value("exponent", "2.1"), c),
            int.Parse(Value("seed", "42"), c), int.Parse(Value("readers", "100"), c), int.Parse(Value("timeout-ms", "5000"), c));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"accounts={Accounts}, mean following={MeanFollowing}, exponent={Exponent}, seed={Seed}, readers={Readers}, timeout={TimeoutMs} ms");
}

internal sealed record Graph(int Accounts, Guid[] Ids, List<(int From, int To)> Edges, int[] Followers, int[] Following)
{
    public int MaxFollowers => Followers.Max();

    public int Supernodes => Followers.Count(f => f >= 10_000);
}

/// <summary>
/// Out-degrees (how many each account follows) are drawn from a discrete power law with the given
/// exponent, scaled to the mean and capped at 5,000 (the product's limit). Followees are drawn by
/// rank from a Zipf distribution (exponent 1), so a few accounts gather most followers: celebrities.
/// The same seed always gives the same graph.
/// </summary>
internal static class Generator
{
    public static Graph Build(Options options)
    {
        var random = new Random(options.Seed);
        var n = options.Accounts;
        var ids = Enumerable.Range(0, n).Select(i => Id(options.Seed, i)).ToArray();

        // Pareto out-degrees: x_min * U^(-1/(a-1)), mean = x_min (a-1)/(a-2) for a > 2.
        var xMin = options.MeanFollowing * (options.Exponent - 2) / (options.Exponent - 1);
        var zipf = Zipf(n);
        var edges = new List<(int, int)>();
        var followers = new int[n];
        var following = new int[n];
        var seen = new HashSet<int>();
        for (var from = 0; from < n; from++)
        {
            var degree = (int)Math.Min(5_000, Math.Max(1, Math.Round(xMin * Math.Pow(random.NextDouble(), -1 / (options.Exponent - 1)))));
            seen.Clear();
            for (var tries = 0; seen.Count < Math.Min(degree, n - 1) && tries < degree * 4; tries++)
            {
                var to = Sample(zipf, random);
                if (to != from && seen.Add(to))
                {
                    edges.Add((from, to));
                    followers[to]++;
                    following[from]++;
                }
            }
        }

        return new Graph(n, ids, edges, followers, following);
    }

    /// <summary>A UUIDv7-shaped id that depends only on the seed and the index.</summary>
    private static Guid Id(int seed, int index) =>
        Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + ((long)seed * 10_000_000) + index));

    private static double[] Zipf(int n)
    {
        var cumulative = new double[n];
        var total = 0.0;
        for (var r = 0; r < n; r++)
        {
            total += 1.0 / (r + 1);
            cumulative[r] = total;
        }

        for (var r = 0; r < n; r++)
        {
            cumulative[r] /= total;
        }

        return cumulative;
    }

    private static int Sample(double[] cumulative, Random random)
    {
        var index = Array.BinarySearch(cumulative, random.NextDouble());
        return Math.Min(index < 0 ? ~index : index, cumulative.Length - 1);
    }
}
