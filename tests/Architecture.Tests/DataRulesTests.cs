using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using static Architecture.Tests.OurAssemblies;

namespace Architecture.Tests;

public class DataRulesTests
{
    /// <summary>
    /// Each module's database model lives in its own schema, named after the module: a table of
    /// another module's schema in a model would be a cross-module read waiting to happen. The models
    /// are built by each module's design-time factory, without a database.
    /// </summary>
    [Fact]
    public void Every_database_model_stays_in_its_own_modules_schema()
    {
        var factories = All.SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>)))
            .ToList();
        Assert.NotEmpty(factories);

        foreach (var factory in factories)
        {
            var module = ModuleOf(factory.Assembly.GetName().Name!);
            var create = factory.GetMethod("CreateDbContext")!;
            using var context = (DbContext)create.Invoke(Activator.CreateInstance(factory, nonPublic: true), [Array.Empty<string>()])!;
            var schemas = context.Model.GetEntityTypes().Select(e => e.GetSchema()).Distinct().ToList();
            Assert.Equal([module.ToLowerInvariant()], schemas);
        }
    }
}
