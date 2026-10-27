using System.Reflection;
using System.Text.RegularExpressions;

namespace Recommendations.Infrastructure.Graph;

/// <summary>The named queries of Queries.cypher, read once from the assembly: what runs is what the lint reads.</summary>
internal static partial class CypherQueries
{
    public const string QueriesResource = "Recommendations.Graph.Queries.cypher";
    public const string SetupResource = "Recommendations.Graph.Setup.cypher";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> All = new(() => Parse(Read(QueriesResource)));

    public static string Get(string name) => All.Value.TryGetValue(name, out var query)
        ? query
        : throw new KeyNotFoundException($"Queries.cypher has no query named {name}.");

    public static IReadOnlyDictionary<string, string> Named => All.Value;

    public static string Read(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Splits the file at each <c>// name: X</c> line; comment lines are dropped from the query text.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string file)
    {
        var queries = new Dictionary<string, string>();
        foreach (var block in NameLine().Split(file).Skip(1).Chunk(2))
        {
            var lines = block[1].Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Trim().Length > 0);
            queries[block[0].Trim()] = string.Join('\n', lines).Trim();
        }

        return queries;
    }

    [GeneratedRegex(@"^//\s*name:\s*(\w+)\s*$", RegexOptions.Multiline)]
    private static partial Regex NameLine();
}
