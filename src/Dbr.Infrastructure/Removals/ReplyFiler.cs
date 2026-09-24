// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// Writes a reply against the attempt it answers, acting for that attempt's account.
/// </summary>
/// <remarks>
/// Ordinary path, ordinary role: by the time this runs the account is known and the tenant
/// is set on the scope, so the row goes in under the same boundary everything else does.
/// The privileged half — working out whose reply this was — happened before it and is one
/// statement wide.
/// </remarks>
public sealed class ReplyFiler(DbrDbContext db, TimeProvider clock) : IReplyFiler
{
    public async Task<ReplyFiling> FileAsync(
        InboundMessage message,
        AnsweredDemandMatch match,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(match);

        db.Add(new BrokerReply
        {
            TenantId = match.Demand.TenantId,
            RemovalRequestId = match.Demand.RemovalRequestId,
            RemovalJobId = match.Demand.JobId,
            SourceRef = message.SourceRef,
            MessageId = message.MessageId,
            FromAddress = message.From,
            Subject = message.Subject,
            MatchedBy = match.MatchedBy,
            ReceivedAt = message.ReceivedAt,
            FiledAt = clock.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ReplyFiling.Filed;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same message offered twice, which is what a source doing the right thing
            // produces: it keeps offering a reply until it is acknowledged, so a process
            // that files one and stops before acknowledging sees it again. Answering that
            // it is already filed is what lets the caller acknowledge it and move on —
            // treating it as an error would leave the message in the source forever, being
            // re-read and re-rejected on every pass.
            db.ChangeTracker.Clear();
            return ReplyFiling.AlreadyFiled;
        }
    }
}
