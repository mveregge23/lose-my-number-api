// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Npgsql;

namespace Dbr.CatalogSync;

/// <param name="Applied">Regime rows the files inserted or brought up to date.</param>
/// <param name="Retracted">Regime rows removed because no file describes them any more.</param>
/// <param name="LeftAlone">
/// Rows a file describes that this instance has claimed as its own, regimes and companies
/// alike. Reported rather than forced, and reported loudly enough to notice — an operator
/// who forgot they overrode something would otherwise wonder why a catalog update never
/// arrived.
/// </param>
/// <param name="BrokersApplied">Company rows the files inserted or brought up to date.</param>
/// <param name="BrokersRetracted">
/// Company rows deactivated because no file describes them any more. Deactivated rather
/// than removed: see <see cref="CatalogSyncRunner.RetractBrokersAsync"/>.
/// </param>
public sealed record CatalogSyncResult(
    int Applied,
    int Retracted,
    IReadOnlyList<string> LeftAlone,
    int BrokersApplied,
    int BrokersRetracted);

/// <summary>
/// Applies the curated legal-basis and company files to the database.
/// </summary>
/// <remarks>
/// <para>
/// One transaction, for regimes and companies together. A sync that inserted half its
/// files and then hit a retraction it could not perform would leave the catalog describing
/// a state no file does, and the next deploy would be reconciling from somewhere nobody
/// chose. Regimes go first, because a company's confirmations will point at them.
/// </para>
/// <para>
/// <b>It only ever touches rows it owns.</b> Every write is conditioned on
/// <c>source = 'catalog'</c>, so an instance's own reading of a regime, or its own entry
/// for a company, survives both an update and a retraction of the shared one — which is
/// the whole reason the column exists.
/// </para>
/// </remarks>
public sealed class CatalogSyncRunner(string connectionString)
{
    /// <summary>
    /// What separates the parts of a natural key when several are handed to one statement.
    /// A control character rather than punctuation, so no value that could legitimately
    /// appear in a code or a scope can make two different keys read as one.
    /// </summary>
    private const char KeySeparator = '\u001f';

    public async Task<CatalogSyncResult> RunAsync(
        IReadOnlyList<CatalogRow> rows,
        IReadOnlyList<BrokerRow> brokers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(brokers);

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

        var brokersClaimed = await BrokersLeftAloneAsync(connection, brokers, cancellationToken)
            .ConfigureAwait(false);

        var brokersApplied = 0;

        foreach (var broker in brokers.Where(broker => !brokersClaimed.ContainsKey(broker.Id)))
        {
            brokersApplied += await ApplyBrokerAsync(connection, broker, cancellationToken)
                .ConfigureAwait(false);
        }

        var brokersRetracted = await RetractBrokersAsync(connection, brokers, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CatalogSyncResult(
            applied,
            retracted,
            [.. leftAlone, .. brokersClaimed.Values],
            brokersApplied,
            brokersRetracted);
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
    /// <exception cref="CatalogSyncRefusedException">
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
            .Select(row => $"{row.Code}{KeySeparator}{CatalogVocabulary.ToWire(row.RequestType)}{KeySeparator}{row.ResidencyScope}")
            .ToArray();

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM legal_basis
            WHERE source = 'catalog'
              AND code || @separator || request_type || @separator || residency_scope <> ALL (@keys)
            """,
            connection);

        command.Parameters.AddWithValue("keys", keys);
        command.Parameters.AddWithValue("separator", KeySeparator.ToString());

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException refused)
            when (refused.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new CatalogSyncRefusedException(
                "A regime being retracted still has brokers confirmed against it. Those "
                + "confirmations are a reviewed judgement that the statute applies, so the schema "
                + "refuses to drop them as a side effect. Remove the confirmations deliberately, "
                + "then retract the regime.",
                refused);
        }
    }

    /// <summary>
    /// Companies a file describes that this instance has taken ownership of, by id.
    /// </summary>
    /// <remarks>
    /// Claimed by either half of the identity. A local row under the file's id is the
    /// plain case. A local row under the file's <i>domain</i> and a different id is the
    /// subtler one: the operator described the company before the catalog did, their
    /// recipes bind to their id, and the shared row would collide with theirs on the
    /// domain. Both are the operator's, and both are left exactly as they are.
    /// </remarks>
    private static async Task<Dictionary<Guid, string>> BrokersLeftAloneAsync(
        NpgsqlConnection connection,
        IReadOnlyList<BrokerRow> brokers,
        CancellationToken cancellationToken)
    {
        var claimed = new Dictionary<Guid, string>();

        foreach (var broker in brokers)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT domain FROM broker
                WHERE (id = @id OR domain = @domain) AND source = 'local'
                LIMIT 1
                """,
                connection);

            command.Parameters.AddWithValue("id", broker.Id);
            command.Parameters.AddWithValue("domain", broker.Domain);

            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string held)
            {
                claimed[broker.Id] = $"{broker.Domain} ({broker.Name}) — this instance holds {held}";
            }
        }

        return claimed;
    }

    /// <summary>
    /// Inserts or updates one company. The whole row, every time.
    /// </summary>
    /// <remarks>
    /// Pacing included. The file is the entire description of a catalog company, so an
    /// operator who tunes a lane by hand and leaves the row marked <c>catalog</c> will see
    /// it tuned back on the next deploy — the way to keep a change is to take the row over,
    /// which is one column and is reported at every sync thereafter. The alternative, a
    /// sync that wrote some columns and not others, would make "which of these did the
    /// catalog set" a question with a different answer per column.
    /// </remarks>
    private static async Task<int> ApplyBrokerAsync(
        NpgsqlConnection connection,
        BrokerRow broker,
        CancellationToken cancellationToken)
    {
        // Conflict on the id and not the domain: the id is what recipes and history bind
        // to, and a domain is the thing that gets corrected. catalog_verified_at is not in
        // the list on purpose — it records what this instance has observed, not what the
        // file says.
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO broker
                (id, name, domain, removal_method, sla_days, active,
                 max_concurrency, min_delay_ms, rate_limit_threshold, cooldown_minutes,
                 form_change_threshold, email_contact_mode, source)
            VALUES
                (@id, @name, @domain, @method, @sla, @active,
                 @concurrency, @delay, @rate_limit, @cooldown,
                 @form_change, @contact, 'catalog')
            ON CONFLICT (id) DO UPDATE
                SET name                  = EXCLUDED.name,
                    domain                = EXCLUDED.domain,
                    removal_method        = EXCLUDED.removal_method,
                    sla_days              = EXCLUDED.sla_days,
                    active                = EXCLUDED.active,
                    max_concurrency       = EXCLUDED.max_concurrency,
                    min_delay_ms          = EXCLUDED.min_delay_ms,
                    rate_limit_threshold  = EXCLUDED.rate_limit_threshold,
                    cooldown_minutes      = EXCLUDED.cooldown_minutes,
                    form_change_threshold = EXCLUDED.form_change_threshold,
                    email_contact_mode    = EXCLUDED.email_contact_mode
                WHERE broker.source = 'catalog'
            """,
            connection);

        command.Parameters.AddWithValue("id", broker.Id);
        command.Parameters.AddWithValue("name", broker.Name);
        command.Parameters.AddWithValue("domain", broker.Domain);
        command.Parameters.AddWithValue("method", CatalogVocabulary.ToWire(broker.RemovalMethod));
        command.Parameters.AddWithValue("sla", broker.SlaDays);
        command.Parameters.AddWithValue("active", broker.Active);
        command.Parameters.AddWithValue("concurrency", broker.MaxConcurrency);
        command.Parameters.AddWithValue("delay", broker.MinDelayMs);
        command.Parameters.AddWithValue("rate_limit", broker.RateLimitThreshold);
        command.Parameters.AddWithValue("cooldown", broker.CooldownMinutes);
        command.Parameters.AddWithValue("form_change", broker.FormChangeThreshold);
        command.Parameters.AddWithValue("contact", CatalogVocabulary.ToWire(broker.EmailContactMode));

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException refused)
            when (refused.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The file moved a catalog company onto a domain a different catalog row holds.
            // Not the left-alone case, which ran before this: that is a local row claiming
            // the domain outright. This is two catalog ids disagreeing about which company
            // a domain is, which the reader refuses when both are in the files and which
            // can still happen when one of them has since left them and sits deactivated.
            throw new CatalogSyncRefusedException(
                $"{broker.Domain} is already the domain of another company on this instance, "
                + $"and the catalog now describes it as {broker.Name} ({broker.Id}). Two rows "
                + "for one domain would be two lanes for one company, so the schema refuses. "
                + "Decide which row is the company, then run the sync again.",
                refused);
        }
    }

    /// <summary>
    /// Deactivates catalog companies no file describes any more.
    /// </summary>
    /// <remarks>
    /// Deactivates rather than deletes, which is where companies part ways with regimes.
    /// A regime can be deleted because nothing operational points at it. A company is
    /// pointed at by every scan that asked it, every finding on its site and every demand
    /// sent to it, none of which cascade — a demand somebody sent is history, and history
    /// that names a company has to keep naming it. On any instance that ever used the
    /// company, deletion would be refused by the schema and would fail the deploy over a
    /// scan somebody ran months ago. So the row stays, stops being searched, paced or
    /// sent to, and a file that comes back reactivates it under the same id.
    /// </remarks>
    private static async Task<int> RetractBrokersAsync(
        NpgsqlConnection connection,
        IReadOnlyList<BrokerRow> brokers,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE broker
            SET active = false
            WHERE source = 'catalog' AND active AND id <> ALL (@ids)
            """,
            connection);

        command.Parameters.AddWithValue("ids", brokers.Select(broker => broker.Id).ToArray());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A write the schema refused, because it would contradict something already there — a
/// regime brokers are still confirmed against, or a domain another company already holds.
/// Nothing was applied; a person has to decide, and the message says what.
/// </summary>
public sealed class CatalogSyncRefusedException : Exception
{
    public CatalogSyncRefusedException()
    {
    }

    public CatalogSyncRefusedException(string message)
        : base(message)
    {
    }

    public CatalogSyncRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
