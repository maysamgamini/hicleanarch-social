using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

namespace Relationships.Infrastructure.Persistence;

/// <summary>SQL on the context's own connection and inside its open transaction, as it is written.</summary>
internal static class RawSql
{
    public static async Task<NpgsqlCommand> Command(this DbContext db, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    public static async Task<int> Execute(this DbContext db, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = await db.Command(sql, cancellationToken, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<List<T>> Read<T>(this DbContext db, string sql, Func<NpgsqlDataReader, T> row, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = await db.Command(sql, cancellationToken, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(row(reader));
        }

        return rows;
    }
}
