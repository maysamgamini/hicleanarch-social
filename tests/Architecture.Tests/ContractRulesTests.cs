using System.Runtime.CompilerServices;

using static Architecture.Tests.OurAssemblies;

namespace Architecture.Tests;

/// <summary>
/// A module's contracts are what other modules may see of it: commands, queries, their results and
/// events. Records only: data with value equality and no behaviour, so nothing of a module's rules
/// can leak through them.
/// </summary>
public class ContractRulesTests
{
    public static TheoryData<string> Contracts() => [.. All.Select(a => a.GetName().Name!).Where(n => LayerOf(n) == "Contracts")];

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Contracts_hold_records_only(string name)
    {
        var types = Named(name).GetTypes().Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), false) && !t.IsNested);
        Assert.All(types, t => Assert.True(IsRecord(t), $"{t.FullName} in {name} is not a record"));
    }

    // The compiler gives every record class a hidden clone method; no other class has one.
    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;
}
