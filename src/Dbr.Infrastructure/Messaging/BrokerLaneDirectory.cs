// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using Npgsql;

namespace Dbr.Infrastructure.Messaging;

/// <summary>
/// Reads each active broker's pacing straight from the catalog.
/// </summary>
/// <remarks>
/// <para>
/// A raw query rather than the catalog service, for the same reason the account directory
/// is one: this runs while the bus is being configured, before there is a request scope to
/// resolve a <c>DbContext</c> in, and it reads four columns once. The catalog carries no
/// row-level security — a broker is a company and belongs to nobody — so this needs no
/// tenant and establishes none.
/// </para>
/// <para>
/// Inactive brokers get no lane. An entry an operator has deactivated is one this instance
/// has decided not to dispatch against, and a queue standing ready for it would accept
/// work that nothing would ever drain.
/// </para>
/// </remarks>
public sealed class BrokerLaneDirectory(string connectionString) : IBrokerLaneDirectory
{
    public async Task<IReadOnlyList<BrokerLane>> ListLanesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Ordered so two processes configuring the same bus declare the same endpoints in
        // the same order, which makes a startup log from one comparable with another's.
        await using var command = new NpgsqlCommand(
            """
            SELECT id, max_concurrency, min_delay_ms
            FROM public.broker
            WHERE active
            ORDER BY id;
            """,
            connection);

        var lanes = new List<BrokerLane>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lanes.Add(new BrokerLane(
                reader.GetGuid(0),
                reader.GetInt32(1),
                TimeSpan.FromMilliseconds(reader.GetInt32(2))));
        }

        return lanes;
    }
}
