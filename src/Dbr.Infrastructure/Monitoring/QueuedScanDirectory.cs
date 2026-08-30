// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Npgsql;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Reads the runs nobody has started as <c>dbr_scheduler</c>, and nothing else ever.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a <c>DbContext</c>.</b> The same reasoning the account directory
/// records: a context acting as this role would be a mapped model somebody could add an
/// entity to, and the privilege it holds — seeing past the tenant boundary — is exactly the
/// privilege that should not have a comfortable surface to grow on. One connection, one
/// statement, two columns.
/// </para>
/// <para>
/// The policy behind it only shows rows that are still waiting, so the <c>WHERE</c> below
/// is not what makes that true — it is there so the query says what it means, and so a
/// reader does not have to go and find the policy to know why finished runs never appear.
/// </para>
/// </remarks>
public sealed class QueuedScanDirectory(string connectionString) : IQueuedScanDirectory
{
    public async Task<IReadOnlyList<QueuedScan>> ListQueuedAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var role = new NpgsqlCommand("SET ROLE dbr_scheduler;", connection);
        await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Oldest first, so a backlog drains in the order people asked rather than leaving
        // the earliest request behind every pass.
        await using var command = new NpgsqlCommand(
            """
            SELECT id, tenant_id
            FROM public.scan
            WHERE status = 'queued'
            ORDER BY requested_at, id
            LIMIT @limit;
            """,
            connection);

        command.Parameters.AddWithValue("limit", limit);

        var queued = new List<QueuedScan>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            queued.Add(new QueuedScan(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return queued;
    }
}
