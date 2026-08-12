// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// Runs a statement EF's query pipeline has no way to express, over the context's own
/// connection.
/// </summary>
/// <remarks>
/// <para>
/// Going through the context rather than opening a fresh <c>NpgsqlConnection</c> is
/// the entire point: the connection EF hands out has been through the tenant
/// interceptor, so it is acting as the restricted application role and carries
/// whatever tenant this unit of work established. A connection opened on the side
/// would arrive as the owning superuser, for whom the row-level security policies do
/// not apply at all — the statement would work, and would silently be running outside
/// the boundary every other query is inside.
/// </para>
/// <para>
/// The connection is closed again before returning. It matters here more than usual:
/// callers of this run <em>before</em> the tenant is known, and a connection left open
/// across the moment authentication succeeds would still be carrying the empty tenant
/// it opened with, since the interceptor only writes that setting when a connection
/// opens.
/// </para>
/// </remarks>
internal static class DbrDbContextCommandExtensions
{
    public static async Task<TResult> ExecuteCommandAsync<TResult>(
        this DbrDbContext context,
        string sql,
        Func<DbCommand, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(execute);

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;

            return await execute(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Adds a parameter, since every caller here needs at least one.</summary>
    public static DbCommand WithParameter(this DbCommand command, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);

        return command;
    }
}
