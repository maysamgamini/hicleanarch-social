using System.Text.RegularExpressions;

namespace Graph.Tests;

/// <summary>
/// The openCypher subset this course allows: what both Neo4j 5.26 and Amazon Neptune run. The refusals
/// follow Neptune's "openCypher specification compliance in Amazon Neptune" page (read 2026-10-27):
/// no procedures (CALL ... YIELD, so no APOC and no GDS), no shortestPath or allShortestPaths, no
/// MANDATORY MATCH, no COUNT {}, EXISTS {} or COLLECT {} subqueries and no pattern comprehensions
/// (Neo4j-only), no ^, no SKIP or LIMIT computed from data, no UNION in a write, no user-defined
/// functions, no engine-specific ids (elementId, Neptune's ~id), no schema statements outside the
/// Neo4j adapter's setup step, no datetime arithmetic, and no size() over a pattern.
/// </summary>
public static partial class CypherLint
{
    private static readonly (string Rule, Regex Pattern)[] Refused =
    [
        ("CALL or YIELD (procedures)", new(@"\b(CALL|YIELD)\b", RegexOptions.IgnoreCase)),
        ("apoc or gds", new(@"\b(apoc|gds)\.", RegexOptions.IgnoreCase)),
        ("shortestPath", new(@"\b(all)?shortestPaths?\s*\(", RegexOptions.IgnoreCase)),
        ("MANDATORY MATCH", new(@"\bMANDATORY\s+MATCH\b", RegexOptions.IgnoreCase)),
        ("COUNT, EXISTS or COLLECT subquery", new(@"\b(COUNT|EXISTS|COLLECT)\s*\{", RegexOptions.IgnoreCase)),
        ("pattern comprehension", new(@"\[\s*\(.*?\|", RegexOptions.Singleline)),
        ("^ (exponent)", new(@"\^")),
        ("SKIP or LIMIT not a constant or a parameter", new(@"\b(SKIP|LIMIT)\s+(?!\d+\b|\$\w+\b)", RegexOptions.IgnoreCase)),
        ("elementId or ~id", new(@"\belementId\s*\(|~id", RegexOptions.IgnoreCase)),
        ("CREATE INDEX or CREATE CONSTRAINT", new(@"\bCREATE\s+(INDEX|CONSTRAINT)\b", RegexOptions.IgnoreCase)),
        ("datetime arithmetic", new(@"\b(datetime|date|duration)\s*\([^)]*\)\s*[+-]", RegexOptions.IgnoreCase)),
        ("size() over a pattern", new(@"\bsize\s*\(\s*\(", RegexOptions.IgnoreCase)),
    ];

    private static readonly Regex Write = new(@"\b(CREATE|MERGE|SET|DELETE|REMOVE)\b", RegexOptions.IgnoreCase);

    private static readonly Regex Union = new(@"\bUNION\b", RegexOptions.IgnoreCase);

    /// <summary>The rules a query breaks; empty when it is inside the subset.</summary>
    public static IReadOnlyList<string> Violations(string query)
    {
        var text = StripStrings(query);
        var broken = Refused.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Rule).ToList();
        if (Union.IsMatch(text) && Write.IsMatch(text))
        {
            broken.Add("UNION in a query that writes");
        }

        return broken;
    }

    // String literals may hold any text; comments are already out of the parsed queries.
    private static string StripStrings(string query) => StringLiteral().Replace(query, "''");

    [GeneratedRegex(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();
}
