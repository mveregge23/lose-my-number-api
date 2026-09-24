// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Domain.Removals;
using Npgsql;

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// Resolves a reply to the attempt it answers as <c>dbr_scheduler</c>, and nothing else ever.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a <c>DbContext</c>.</b> The same reasoning the queued-removal and
/// account directories record: a context acting as this role would be a mapped model
/// somebody could add an entity to, and the privilege it holds — seeing past the tenant
/// boundary — is exactly the privilege that should not have a comfortable surface to grow
/// on. One connection, one statement, four columns.
/// </para>
/// <para>
/// <b>One statement for both keys.</b> A message may name an attempt by the address it was
/// sent to or by an id it quotes, and it frequently carries both. Asking twice would be
/// two round trips to learn which of two things a single message already said.
/// </para>
/// </remarks>
public sealed class AnsweredDemandDirectory(string connectionString) : IAnsweredDemandDirectory
{
    public async Task<AnsweredDemandMatch?> ResolveAsync(
        ReplyEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.IsEmpty)
        {
            return null;
        }

        // Asking about every id a long thread quotes would be a question about every
        // message a company has ever sent us. The ones that could be ours are at the end,
        // which is the order the evidence arrives in.
        var quoted = evidence.QuotedMessageIds.Take(QuotedIdsConsidered).ToArray();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var role = new NpgsqlCommand("SET ROLE dbr_scheduler;", connection);
        await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            SELECT id, tenant_id, removal_request_id, sent_message_id
            FROM public.removal_job
            WHERE id = @job
               OR sent_message_id = ANY(@quoted);
            """,
            connection);

        command.Parameters.AddWithValue("job", evidence.JobId ?? Guid.Empty);
        command.Parameters.AddWithValue("quoted", quoted);

        var byAddress = default(AnsweredDemand);
        var byThread = default(AnsweredDemand);
        var nearestQuoted = int.MaxValue;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var demand = new AnsweredDemand(reader.GetGuid(0), reader.GetGuid(2), reader.GetGuid(1));

            if (evidence.JobId is { } named && demand.JobId == named)
            {
                byAddress = demand;
                continue;
            }

            // A thread can quote several of our own messages when a company has been
            // written to more than once about the same listing. The nearest one is the
            // attempt being answered; the others are history the client kept.
            var position = Array.IndexOf(quoted, reader.GetString(3));
            if (position >= 0 && position < nearestQuoted)
            {
                nearestQuoted = position;
                byThread = demand;
            }
        }

        // The address wins when both resolve. It names one attempt and cannot name another,
        // while a quoted id is the sender's claim about what it is answering.
        return byAddress is not null
            ? new AnsweredDemandMatch(byAddress, ReplyMatch.MailboxAddress)
            : byThread is not null
                ? new AnsweredDemandMatch(byThread, ReplyMatch.ThreadHeaders)
                : null;
    }

    /// <summary>
    /// How far back along a quoted thread this is willing to look.
    /// </summary>
    /// <remarks>
    /// A ticketing system's <c>References</c> grows with every message on the ticket, and
    /// the only entries that could be ours are near the end — which is where the evidence
    /// puts them. A bound keeps a company with a hundred-message thread from turning one
    /// arriving reply into a hundred-term query.
    /// </remarks>
    private const int QuotedIdsConsidered = 16;
}
