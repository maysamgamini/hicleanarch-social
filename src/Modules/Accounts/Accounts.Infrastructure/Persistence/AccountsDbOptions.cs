using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Accounts.Infrastructure.Persistence;

internal static class AccountsDbOptions
{
    public static DbContextOptionsBuilder<AccountsDbContext> Configure(DbContextOptionsBuilder<AccountsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DatabaseMigrator.MigrationsHistory, AccountsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args) =>
        new(AccountsDbOptions.Configure(new DbContextOptionsBuilder<AccountsDbContext>(), "Host=localhost").Options);
}

/// <summary>Brings the Accounts schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class AccountsMigrator : IModuleMigrator
{
    public string Module => "Accounts";

    public string Schema => AccountsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var db = new AccountsDbContext(AccountsDbOptions.Configure(new DbContextOptionsBuilder<AccountsDbContext>(), ownerConnectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
