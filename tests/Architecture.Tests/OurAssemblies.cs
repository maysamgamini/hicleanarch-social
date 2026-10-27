using System.Reflection;

namespace Architecture.Tests;

/// <summary>The compiled assemblies of this repository that ship with the host, and which module each belongs to.</summary>
internal static class OurAssemblies
{
    public static readonly string[] Modules = ["Accounts", "Relationships", "Posts", "Timeline", "Recommendations", "Notifications"];

    public static readonly Assembly[] All = LoadAll();

    /// <summary><c>Accounts.Domain</c> → Accounts; <c>Social.BuildingBlocks.Domain</c> → BuildingBlocks; <c>Social.Host</c> → Host.</summary>
    public static string ModuleOf(string assemblyName) =>
        assemblyName.StartsWith("Social.", StringComparison.Ordinal) ? assemblyName.Split('.')[1] : assemblyName.Split('.')[0];

    /// <summary><c>Accounts.Domain</c> → Domain; <c>Notifications</c> → the empty string.</summary>
    public static string LayerOf(string assemblyName) =>
        assemblyName.Contains('.', StringComparison.Ordinal) ? assemblyName[(assemblyName.LastIndexOf('.') + 1)..] : "";

    public static bool IsOurs(string name) =>
        name == "Social.Host" || name.StartsWith("Social.BuildingBlocks.", StringComparison.Ordinal) || Modules.Contains(ModuleOf(name));

    public static Assembly Named(string name) => All.Single(a => a.GetName().Name == name);

    /// <summary>The assemblies of ours that <paramref name="name"/> references directly.</summary>
    public static IEnumerable<string> ReferencesOf(string name) =>
        Named(name).GetReferencedAssemblies().Select(r => r.Name!).Where(IsOurs);

    private static Assembly[] LoadAll() =>
        [.. Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => IsOurs(n!))
            .Select(n => Assembly.Load(n!))];
}
