using Accounts.Domain;

using Microsoft.EntityFrameworkCore;

namespace Accounts.Infrastructure.Persistence;

/// <summary>The Accounts module's view of the database: its own schema, and nothing else.</summary>
internal sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public const string Schema = "accounts";

    /// <summary>The unique index on <c>lower(handle)</c>, created by the first migration in SQL.</summary>
    public const string HandleIndex = "accounts_handle_lower";

    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Account>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(a => a.Id);
            account.Property(a => a.Id).ValueGeneratedNever();
            account.Property(a => a.Handle)
                .HasConversion(h => h.Value, value => Handle.FromStored(value))
                .HasMaxLength(Handle.MaxLength);
            account.Property(a => a.DisplayName).HasMaxLength(Account.DisplayNameMaxLength);
            account.Property(a => a.Status)
                .HasConversion(s => s.ToString().ToUpperInvariant(), s => Enum.Parse<AccountStatus>(s, true))
                .HasMaxLength(20);
        });
    }
}
