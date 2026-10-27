using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

namespace Integration.Tests;

public sealed class AccountsPersistenceTests(Postgres postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task A_profile_survives_a_restart()
    {
        var me = Guid.NewGuid();
        await using (var first = TestApp.Start(Db))
        {
            var registered = await TestApp.Client(first, me).PutAsJsonAsync("/v1/me", new { handle = "Hedy", display_name = "Hedy Lamarr" }, Ct);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }

        var read = await TestApp.Client(StartHost()).GetFromJsonAsync<JsonElement>("/v1/accounts/hedy", Ct);

        Assert.Equal(me, read.GetProperty("account_id").GetGuid());
        Assert.Equal("Hedy Lamarr", read.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Two_registrations_of_one_handle_in_different_case_give_one_201_and_one_409()
    {
        var host = StartHost();

        var replies = await Task.WhenAll(
            TestApp.Client(host, Guid.NewGuid()).PutAsJsonAsync("/v1/me", new { handle = "katherine", display_name = "K" }, Ct),
            TestApp.Client(host, Guid.NewGuid()).PutAsJsonAsync("/v1/me", new { handle = "KATHERINE", display_name = "J" }, Ct));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], replies.Select(r => r.StatusCode).Order());
        var refused = replies.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("handle_taken", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM accounts.accounts WHERE lower(handle) = 'katherine'"));
    }

    [Fact]
    public async Task The_accounts_role_cannot_read_another_schema()
    {
        await Db.Execute("CREATE SCHEMA other; CREATE TABLE other.secrets (id int);");

        await using var accounts = new NpgsqlConnection(Db.For("Accounts"));
        await accounts.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT count(*) FROM other.secrets", accounts);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_accounts_role_cannot_create_tables_or_write_its_migration_history()
    {
        await using var accounts = new NpgsqlConnection(Db.For("Accounts"));
        await accounts.OpenAsync(Ct);
        await using var create = new NpgsqlCommand("CREATE TABLE accounts.sneaky (id int)", accounts);
        await using var history = new NpgsqlCommand(
            "INSERT INTO accounts.__ef_migrations (migration_id, product_version) VALUES ('29991231000000_Sneaky', '10.0.0')", accounts);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync(Ct))).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => history.ExecuteNonQueryAsync(Ct))).SqlState);
    }

    [Fact]
    public async Task The_accounts_role_has_no_use_of_the_public_schema_and_no_temporary_tables()
    {
        await using var accounts = new NpgsqlConnection(Db.For("Accounts"));
        await accounts.OpenAsync(Ct);
        await using var temp = new NpgsqlCommand("CREATE TEMP TABLE sneaky (id int)", accounts);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => temp.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_host_does_not_migrate_at_start()
    {
        var empty = await Server.NewDatabase();
        await empty.Execute("DROP SCHEMA accounts CASCADE");

        await using var host = TestApp.Start(empty);
        var reply = await TestApp.Client(host).GetAsync("/v1/accounts/anyone", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, reply.StatusCode);
        Assert.False(await empty.Scalar<bool>("SELECT EXISTS (SELECT FROM pg_namespace WHERE nspname = 'accounts')"));
    }
}
