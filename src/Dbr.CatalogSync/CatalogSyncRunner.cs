// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Npgsql;

namespace Dbr.CatalogSync;

/// <param name="Applied">Rows the files inserted or brought up to date.</param>
/// <param name="Retracted">Rows removed because no file describes them any more.</param>
/// <param name="LeftAlone">
/// Rows a file describes that this instance has claimed as its own. Reported rather than
/// forced, and reported loudly enough to notice — an operator who forgot they overrode
/// something would otherwise wonder why a catalog update never arrived.
/// </param>
public sealed record CatalogSyncResult(int Applied, int Retracted, IReadOnlyList<string> LeftAlone);

/// <summary>
/// Applies the curated legal-basis files to the database.
/// </summary>
/// <remarks>
/// <para>
/// One transaction. A sync that inserted half its files and then hit a retraction it
/// could not perform would leave the catalog describing a state no file does, and the
/// next deploy would be reconciling from somewhere nobody chose.
/// </para>
/// <para>
/// <b>It only ever touches rows it owns.</b> Every write is conditioned on
/// <c>source = 'catalog'</c>, so an instance's own reading of a regime survives both an
/// update and a retraction of the shared one — which is the whole reason the column
/// exists.
/// </para>
/// </remarks>
public sealed class CatalogSyncRunner(string connectionString)
{
    public async Task<CatalogSyncResult> RunAsync(
        IReadOnlyList<CatalogRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var leftAlone = await LeftAloneAsync(connection, rows, cancellationToken).ConfigureAwait(false);

        var applied = 0;

        foreach (var row in rows)
        {
            applied += await ApplyAsync(connection, row, cancellationToken).ConfigureAwait(false);
        }

        var retracted = await RetractAsync(connection, rows, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CatalogSyncResult(applied, retracted, leftAlone);
    }

    /// <summary>Rows a file describes that this instance has taken ownership of.</summary>
    private static async Task<List<string>> LeftAloneAsync(
        NpgsqlConnection connection,
        IReadOnlyList<CatalogRow> rows,
        CancellationToken cancellationToken)
    {
        var claimed = new List<string>();

        foreach (var row in rows)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT 1 FROM legal_basis
                WHERE code = @code AND request_type = @request_type AND residency_scope = @scope
                  AND source = 'local'
                """,
                connection);

            command.Parameters.AddWithValue("code", row.Code);
            command.Parameters.AddWithValue("request_type", CatalogVocabulary.ToWire(row.RequestType));
            command.Parameters.AddWithValue("scope", row.ResidencyScope);

            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                claimed.Add(
                    $"{row.Code} / {CatalogVocabulary.ToWire(row.RequestType)} / {row.ResidencyScope}");
            }
        }

        return claimed;
    }

    private static async Task<int> ApplyAsync(
        NpgsqlConnection connection,
        CatalogRow row,
        CancellationToken cancellationToken)
    {
        // The conflict target is the natural key the schema already enforces, so a file
        // describing a regime this instance already has updates it rather than colliding.
        // The WHERE on the update is what keeps a local row local: the insert loses the
        // race it was always going to lose, and nothing is overwritten.
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO legal_basis
                (code, request_type, residency_scope, response_deadline_days, extension_days,
                 deadline_unit, verification_level, citation_url, reviewed_at, reviewed_by, source)
            VALUES
                (@code, @request_type, @scope, @days, @extension,
                 @unit, @verification, @citation, @reviewed_at, @reviewed_by, 'catalog')
            ON CONFLICT (code, request_type, residency_scope) DO UPDATE
                SET response_deadline_days = EXCLUDED.response_deadline_days,
                    extension_days         = EXCLUDED.extension_days,
                    deadline_unit          = EXCLUDED.deadline_unit,
                    verification_level     = EXCLUDED.verification_level,
                    citation_url           = EXCLUDED.citation_url,
                    reviewed_at            = EXCLUDED.reviewed_at,
                    reviewed_by            = EXCLUDED.reviewed_by
                WHERE legal_basis.source = 'catalog'
            """,
            connection);

        command.Parameters.AddWithValue("code", row.Code);
        command.Parameters.AddWithValue("request_type", CatalogVocabulary.ToWire(row.RequestType));
        command.Parameters.AddWithValue("scope", row.ResidencyScope);
        command.Parameters.AddWithValue("days", row.ResponseDeadlineDays);
        command.Parameters.AddWithValue("extension", row.ExtensionDays);
        command.Parameters.AddWithValue("unit", CatalogVocabulary.ToWire(row.DeadlineUnit));
        command.Parameters.AddWithValue("verification", CatalogVocabulary.ToWire(row.VerificationLevel));
        command.Parameters.AddWithValue("citation", row.CitationUrl);
        command.Parameters.AddWithValue("reviewed_at", row.ReviewedAt);
        command.Parameters.AddWithValue("reviewed_by", row.ReviewedBy);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes catalog rows no file describes any more.
    /// </summary>
    /// <remarks>
    /// The case this whole mechanism exists for. A regime read wrongly and corrected has
    /// to be able to stop governing requests, and deleting the file is how somebody says
    /// so — otherwise a retraction means a manual DELETE on every install that ever
    /// pulled the bad content.
    /// </remarks>
    /// <exception cref="CatalogRetractionBlockedException">
    /// Brokers are still confirmed against the regime being retracted. The schema refuses
    /// that deletion deliberately, because the confirmations are somebody's reviewed
    /// judgement that the statute applies and losing them silently is how a removal
    /// quietly downgrades to a courtesy deadline.
    /// </exception>
    private static async Task<int> RetractAsync(
        NpgsqlConnection connection,
        IReadOnlyList<CatalogRow> rows,
        CancellationToken cancellationToken)
    {
        var keys = rows
            .Select(row => $"{row.Code}{CatalogVocabulary.ToWire(row.RequestType)}{row.ResidencyScope}")
            .ToArray();

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM legal_basis
            WHERE source = 'catalog'
              AND code || chr(31) || request_type || chr(31) || residency_scope <> ALL (@keys)
            """,
            connection);

        command.Parameters.AddWithValue("keys", keys);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException refused)
            when (refused.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new CatalogRetractionBlockedException(
                "A regime being retracted still has brokers confirmed against it. Those "
                + "confirmations are a reviewed judgement that the statute applies, so the schema "
                + "refuses to drop them as a side effect. Remove the confirmations deliberately, "
                + "then retract the regime.",
                refused);
        }
    }
}

/// <summary>A retraction the schema refused, because something still depends on the row.</summary>
public sealed class CatalogRetractionBlockedException : Exception
{
    public CatalogRetractionBlockedException()
    {
    }

    public CatalogRetractionBlockedException(string message)
        : base(message)
    {
    }

    public CatalogRetractionBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
