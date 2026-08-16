// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Compares what a <see cref="DbContext"/> believes the schema looks like against
/// what the migrations actually built.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written SQL owning the schema buys reviewable migrations and costs the
/// guarantee that the model and the database still agree. Nothing enforces that
/// agreement — a renamed column in a migration leaves the C# compiling and the tests
/// passing right up until a query runs in production. This is the thing that notices.
/// </para>
/// <para>
/// It reports every disagreement it finds rather than throwing on the first, because
/// a rename usually breaks several columns at once and fixing them one CI run at a
/// time is miserable.
/// </para>
/// </remarks>
public static class SchemaDrift
{
    /// <summary>
    /// Every way <paramref name="context"/>'s model disagrees with the live schema.
    /// Empty means they match.
    /// </summary>
    /// <param name="ownerConnectionString">
    /// Read as the owning role rather than through the context. The application role
    /// is deliberately restricted, and a permission gap would otherwise look
    /// identical to a missing table.
    /// </param>
    public static async Task<IReadOnlyList<string>> DetectAsync(
        DbContext context,
        string ownerConnectionString)
    {
        var problems = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not { } tableName)
            {
                continue;
            }

            var schema = entityType.GetSchema() ?? "public";
            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            var actual = await ReadColumnsAsync(ownerConnectionString, schema, tableName);

            if (actual.Count == 0)
            {
                problems.Add(
                    $"{entityType.ClrType.Name}: expects table {schema}.{tableName}, which does not exist. "
                    + "Either the entity was added without a migration, or a migration renamed the table.");

                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName(storeObject) is not { } columnName)
                {
                    continue;
                }

                if (!actual.TryGetValue(columnName, out var column))
                {
                    problems.Add(
                        $"{entityType.ClrType.Name}.{property.Name}: expects column "
                        + $"{schema}.{tableName}.{columnName}, which does not exist.");

                    continue;
                }

                var expectedType = property.GetColumnType(storeObject);

                if (!TypesMatch(expectedType, column.Type))
                {
                    problems.Add(
                        $"{entityType.ClrType.Name}.{property.Name}: mapped as '{expectedType}' but "
                        + $"{schema}.{tableName}.{columnName} is '{column.Type}'. This is the failure that "
                        + "otherwise surfaces as a cast exception the first time a row is read.");
                }

                if (property.IsNullable != column.IsNullable)
                {
                    problems.Add(
                        $"{entityType.ClrType.Name}.{property.Name}: model says "
                        + $"{(property.IsNullable ? "nullable" : "required")} but "
                        + $"{schema}.{tableName}.{columnName} is "
                        + $"{(column.IsNullable ? "nullable" : "NOT NULL")}.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Runs a real query for every mapped entity, so that anything the metadata
    /// comparison cannot see — a mapping EF rejects, a column it quotes differently —
    /// surfaces as the database refusing the SQL.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ProbeEveryEntityAsync(DbContext context)
    {
        var problems = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
        {
            try
            {
                // Take(1) rather than Any(): it selects every mapped column, so the
                // database validates the whole projection, not just the table's
                // existence.
                var probe = typeof(SchemaDrift)
                    .GetMethod(nameof(QueryOneAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(entityType.ClrType);

                await (Task)probe.Invoke(null, [context])!;
            }
            catch (Exception exception)
            {
                problems.Add($"{entityType.ClrType.Name}: querying it failed — {Unwrap(exception).Message}");
            }
        }

        return problems;
    }

    private static async Task QueryOneAsync<TEntity>(DbContext context)
        where TEntity : class =>
        await context.Set<TEntity>().Take(1).ToListAsync();

    private static Exception Unwrap(Exception exception) =>
        exception is System.Reflection.TargetInvocationException { InnerException: { } inner }
            ? Unwrap(inner)
            : exception;

    /// <summary>
    /// EF and <c>format_type</c> spell the same Postgres types the same way in almost
    /// every case, so this is a plain comparison with the casing and the schema
    /// qualifier that only one side bothers with removed.
    /// </summary>
    private static bool TypesMatch(string expected, string actual) =>
        string.Equals(Normalize(expected), Normalize(actual), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string type) =>
        type.Trim().Replace("pg_catalog.", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, (string Type, bool IsNullable)>> ReadColumnsAsync(
        string connectionString,
        string schema,
        string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // pg_attribute with format_type rather than information_schema.columns: the
        // latter splits "character varying(50)" into a type and a length, which no
        // longer resembles what EF reports.
        //
        // System columns are excluded by attnum, except xmin: a model can legitimately
        // map it as a concurrency token, and a table always has one. Without the
        // exception the detector would report a missing column for a mapping that is
        // correct — and a detector that cries wolf is one nobody believes the next time.
        await using var command = new NpgsqlCommand(
            """
            SELECT a.attname,
                   format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND (a.attnum > 0 OR a.attname = 'xmin')
              AND NOT a.attisdropped
            """,
            connection);

        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var columns = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
        }

        return columns;
    }
}
