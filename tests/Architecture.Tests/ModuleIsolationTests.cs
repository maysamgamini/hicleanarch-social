using static Architecture.Tests.OurAssemblies;

namespace Architecture.Tests;

/// <summary>
/// Modules meet only through contracts: no module references another module's Domain, Application or
/// Infrastructure, so queries and events cross only as the records in Contracts.
/// </summary>
public class ModuleIsolationTests
{
    public static TheoryData<string> ModuleAssemblies() =>
        [.. All.Select(a => a.GetName().Name!).Where(n => Modules.Contains(ModuleOf(n)))];

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void No_module_references_another_modules_domain_application_or_infrastructure(string name)
    {
        var foreign = ReferencesOf(name).Where(r => Modules.Contains(ModuleOf(r)) && ModuleOf(r) != ModuleOf(name));

        Assert.All(foreign, r => Assert.Equal("Contracts", LayerOf(r)));
    }

    [Fact]
    public void Every_module_of_the_spec_is_built() =>
        Assert.All(Modules, m => Assert.Contains(All, a => ModuleOf(a.GetName().Name!) == m));
}
