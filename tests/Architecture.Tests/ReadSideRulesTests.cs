using System.Reflection;

using Social.BuildingBlocks.Application;

using static Architecture.Tests.OurAssemblies;

namespace Architecture.Tests;

/// <summary>
/// A query reads the read side unless it says otherwise (s3-l11). A query handler's ports are read
/// ports (interfaces named *Reads), caches (*Cache, neither side of the database), other modules'
/// contracts and other query handlers; a handler that must see the latest write, or that writes on
/// the way (the feed's rebuild jobs), says so with [ReadsTheWriter].
/// </summary>
public class ReadSideRulesTests
{
    private static readonly Type[] QueryHandlers = [.. All
        .Where(a => LayerOf(a.GetName().Name!) == "Application" && a.GetName().Name != "Social.BuildingBlocks.Application")
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(IsQueryHandler))];

    public static TheoryData<string> Handlers() => [.. QueryHandlers.Select(t => t.FullName!)];

    [Fact]
    public void There_are_query_handlers_to_check()
    {
        Assert.Contains(QueryHandlers, t => t.Name == "GetPostsHandler");
        Assert.Contains(QueryHandlers, t => t.Name == "GetProfilesHandler");
    }

    [Fact]
    public void The_feeds_hydration_queries_read_the_read_side()
    {
        foreach (var name in new[] { "GetPostsHandler", "GetRecentByAuthorHandler", "GetRecentByAuthorsHandler", "GetProfilesHandler" })
        {
            Assert.Null(QueryHandlers.Single(t => t.Name == name).GetCustomAttribute<ReadsTheWriterAttribute>());
        }

        Assert.NotNull(QueryHandlers.Single(t => t.Name == "GetPostHandler").GetCustomAttribute<ReadsTheWriterAttribute>());
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public void A_query_handler_reads_the_read_side_unless_it_is_marked(string handler)
    {
        var type = QueryHandlers.Single(t => t.FullName == handler);
        if (type.GetCustomAttribute<ReadsTheWriterAttribute>() is not null)
        {
            return;
        }

        var ports = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).Where(t => t.IsInterface);
        Assert.All(ports, d => Assert.True(
            d.Name.EndsWith("Reads", StringComparison.Ordinal) || d.Name.EndsWith("Cache", StringComparison.Ordinal)
                || d.Assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal)
                || (d.IsGenericType && d.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)),
            $"{type.Name} depends on {d.Name}, a writer-side port; read through a *Reads port or mark it [ReadsTheWriter]."));
    }

    private static bool IsQueryHandler(Type i) => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>);
}
