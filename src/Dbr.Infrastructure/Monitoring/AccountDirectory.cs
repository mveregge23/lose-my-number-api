// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Npgsql;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Reads the list of account ids as <c>dbr_scheduler</c>, and nothing else ever.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a <c>DbContext</c>.</b> A third context acting as this role would
/// be a mapped model somebody could add an entity to, and the privilege it holds — seeing
/// past the tenant boundary — is exactly the privilege that should not have a comfortable
/// surface to grow on. One connection, one statement, one column. Anything more would
/// have to be written here, deliberately, in front of this comment.
/// </para>
/// <para>
/// The role is set on the connection the same way the interceptors do it, and the grant
/// behind it is column-level, so this could not read an email address even if the query
/// asked for one.
/// </para>
/// </remarks>
public sealed class AccountDirectory(string connectionString) : IAccountDirectory
{
    public async Task<IReadOnlyList<Guid>> ListAccountIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var role = new NpgsqlCommand("SET ROLE dbr_scheduler;", connection);
        await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Ordered so a run is reproducible and a log of one can be read against another.
        await using var command = new NpgsqlCommand(
            "SELECT id FROM public.tenant ORDER BY id;",
            connection);

        var ids = new List<Guid>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
