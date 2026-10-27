using System.Xml.Linq;

namespace Architecture.Tests;

/// <summary>
/// The BuildingBlocks are a package (s3-l15): every project outside src/BuildingBlocks references them
/// as Social.BuildingBlocks.* packages from the feed, never as projects, so a module builds against a
/// version, as it would against any library.
/// </summary>
public class PackageRulesTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Social.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Social.slnx not found above the test's directory.");
    }

    private static readonly string[] ProjectFiles = [.. Directory.GetFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("src/BuildingBlocks/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal)
                && !p.Contains("/obj/", StringComparison.Ordinal) && !p.StartsWith("artifacts/", StringComparison.Ordinal))];

    public static TheoryData<string> Projects() => [.. ProjectFiles];

    [Fact]
    public void There_are_projects_to_check() => Assert.Contains("src/Host/Social.Host.csproj", ProjectFiles);

    [Theory]
    [MemberData(nameof(Projects))]
    public void No_project_references_a_BuildingBlocks_project(string project)
    {
        var references = XDocument.Load(Path.Combine(Root, project)).Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include") ?? "");
        Assert.DoesNotContain(references, r => r.Contains("BuildingBlocks", StringComparison.Ordinal));
    }

    [Fact]
    public void The_solution_builds_the_modules_without_the_BuildingBlocks_projects() =>
        Assert.DoesNotContain("src/BuildingBlocks/", File.ReadAllText(Path.Combine(Root, "Social.slnx")), StringComparison.Ordinal);
}
