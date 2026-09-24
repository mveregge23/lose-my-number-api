// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Domain.Removals;

/// <summary>The attempt a reply belongs to, and the account it belongs to.</summary>
/// <param name="JobId">The attempt.</param>
/// <param name="RemovalRequestId">The demand it is an attempt at.</param>
/// <param name="TenantId">The account, which is what a reply arrives without.</param>
public sealed record AnsweredDemand(Guid JobId, Guid RemovalRequestId, Guid TenantId);

/// <summary>An attempt a reply resolved to, and what resolved it.</summary>
public sealed record AnsweredDemandMatch(AnsweredDemand Demand, ReplyMatch MatchedBy);

/// <summary>
/// Answers which attempt a reply belongs to, across accounts it is not acting for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this reaches past the tenant boundary at all.</b> A message arrives with no
/// account attached to it — a company answers an address or quotes an id, and neither says
/// whose demand it was. The alternative is to ask every account whether this reply is
/// theirs, which is the same shape the dispatcher rejected: cost growing with how many
/// people use the service rather than with how much work there is, and every account
/// answering a question about somebody else's mail.
/// </para>
/// <para>
/// So this is the fourth narrow question the scheduler role is allowed, and the narrowest:
/// four columns of attempts that actually sent something, and nothing about the demand
/// beyond which one it was. Everything with a consequence happens afterwards, inside a
/// scope acting for the one account, through the ordinary path.
/// </para>
/// <para>
/// <b>Both keys in one question.</b> A reply may name an attempt by the address it was
/// sent to, or by the id it quotes, and asking separately would mean two round trips to
/// learn which of two things a single message already told us. The priority between them
/// is decided here rather than by the database: the address is exact, so it wins when both
/// resolve.
/// </para>
/// </remarks>
public interface IAnsweredDemandDirectory
{
    /// <summary>
    /// The attempt this evidence names, or <see langword="null"/> when it names none of
    /// ours.
    /// </summary>
    /// <remarks>
    /// Answering <see langword="null"/> is ordinary rather than exceptional. A mailbox
    /// that demands are sent from receives everything anybody addresses to it, most of
    /// which was never an answer to anything.
    /// </remarks>
    Task<AnsweredDemandMatch?> ResolveAsync(ReplyEvidence evidence, CancellationToken cancellationToken);
}
