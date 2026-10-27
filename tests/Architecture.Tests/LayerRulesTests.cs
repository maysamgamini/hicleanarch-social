using static Architecture.Tests.OurAssemblies;

namespace Architecture.Tests;

/// <summary>
/// The dependency rule, checked on the compiled assemblies: references point inward, from
/// Infrastructure to Application to Domain. A project reference that breaks a rule compiles; these
/// tests are what refuse it.
/// </summary>
public class LayerRulesTests
{
    public static TheoryData<string> Assemblies() => [.. All.Select(a => a.GetName().Name!)];

    [Fact]
    public void The_host_the_building_blocks_and_every_layer_of_every_module_are_checked() =>
        Assert.Superset(
            new HashSet<string>
            {
                "Social.Host", "Social.BuildingBlocks.Domain", "Social.BuildingBlocks.Application", "Social.BuildingBlocks.Infrastructure",
                "Accounts.Domain", "Accounts.Application", "Accounts.Infrastructure", "Accounts.Contracts",
                "Relationships.Domain", "Relationships.Application", "Relationships.Infrastructure", "Relationships.Contracts",
                "Posts.Domain", "Posts.Application", "Posts.Infrastructure", "Posts.Contracts",
                "Timeline.Domain", "Timeline.Application", "Timeline.Infrastructure", "Timeline.Contracts",
                "Recommendations.Domain", "Recommendations.Application", "Recommendations.Infrastructure", "Recommendations.Contracts",
                "Notifications",
            },
            All.Select(a => a.GetName().Name!).ToHashSet());

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void A_domain_depends_only_on_the_shared_domain(string name)
    {
        if (LayerOf(name) != "Domain")
        {
            return;
        }

        Assert.All(ReferencesOf(name), r => Assert.Equal("Social.BuildingBlocks.Domain", r));
    }

    /// <summary>
    /// An application layer asks for what it needs through its own ports. It never names another
    /// module's contracts: the adapters in Infrastructure do, so a later stage can swap an in-process
    /// query for a copy or a network call without touching the use cases.
    /// </summary>
    [Theory]
    [MemberData(nameof(Assemblies))]
    public void An_application_layer_depends_only_on_its_own_domain_its_own_contracts_and_the_shared_blocks(string name)
    {
        if (LayerOf(name) != "Application" || ModuleOf(name) == "BuildingBlocks")
        {
            return;
        }

        var module = ModuleOf(name);
        var allowed = new[] { "Social.BuildingBlocks.Domain", "Social.BuildingBlocks.Application", module + ".Domain", module + ".Contracts" };
        Assert.All(ReferencesOf(name), r => Assert.True(allowed.Contains(r), $"{name} references {r}"));
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void A_contract_depends_only_on_the_shared_application_blocks(string name)
    {
        if (LayerOf(name) != "Contracts")
        {
            return;
        }

        Assert.All(ReferencesOf(name), r => Assert.Equal("Social.BuildingBlocks.Application", r));
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void An_infrastructure_layer_depends_only_on_its_own_module_the_shared_blocks_and_contracts(string name)
    {
        if (LayerOf(name) != "Infrastructure" || ModuleOf(name) == "BuildingBlocks")
        {
            return;
        }

        var module = ModuleOf(name);
        Assert.All(ReferencesOf(name), r => Assert.True(
            ModuleOf(r) == module || ModuleOf(r) == "BuildingBlocks" || LayerOf(r) == "Contracts",
            $"{name} references {r}"));
    }

    /// <summary>
    /// Notifications is one project on purpose: it has no rules of its own to protect with layers. It
    /// may still see other modules only through their contracts.
    /// </summary>
    [Fact]
    public void Notifications_is_one_project_that_sees_others_only_through_contracts() =>
        Assert.All(ReferencesOf("Notifications"), r => Assert.True(
            ModuleOf(r) == "BuildingBlocks" || LayerOf(r) == "Contracts", $"Notifications references {r}"));

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Building_blocks_know_no_module(string name)
    {
        if (ModuleOf(name) != "BuildingBlocks")
        {
            return;
        }

        Assert.DoesNotContain(ReferencesOf(name), r => Modules.Contains(ModuleOf(r)));
    }
}
