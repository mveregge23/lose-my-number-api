// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Removals;

/// <summary>
/// A company answered, and this is the attempt it answered.
/// </summary>
/// <remarks>
/// <para>
/// The row exists so that a demand waiting on a company and a demand nobody is going to
/// answer stop being the same record. Until now every demand sat at <c>awaiting broker
/// response</c> whatever happened next: one being worked on, one closed for want of a
/// reply, and one that bounced and never arrived all read identically while the deadline
/// ran on each.
/// </para>
/// <para>
/// <b>No body, and that is a decision rather than an omission.</b> What a company writes
/// back is prose of its own composition, and the first real one quoted the request it was
/// answering — so a reply body is as likely to hold somebody's name and address as a
/// demand is, and this table is in the store the ordinary path reads. Where that prose
/// belongs, if anywhere, is the question the classification that has to read it will have
/// to settle. Until then this records that an answer came, from whom, and to which
/// attempt, which is what the record was missing.
/// </para>
/// <para>
/// <b><see cref="MatchedBy"/> is kept because it is a fact about the deployment.</b> A
/// reply resolved by the address it was sent to means this instance's per-attempt return
/// address survived the journey out and back. One resolved by thread headers means it may
/// not have — a relay that permits only its own authenticated sender rewrites the address
/// on the way out, silently, and nothing on the sending side can observe that. This column
/// is where it becomes observable.
/// </para>
/// </remarks>
public class BrokerReply : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account whose demand was answered.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The demand this answers.</summary>
    public required Guid RemovalRequestId { get; init; }

    /// <summary>The attempt this answers.</summary>
    public required Guid RemovalJobId { get; init; }

    /// <summary>
    /// What the source that delivered this called it.
    /// </summary>
    /// <remarks>
    /// Kept so that the same message arriving twice is refused rather than filed twice. A
    /// source offers a message until it is acknowledged, and a process that files a reply
    /// and stops before acknowledging it will be offered that reply again — which is the
    /// right way round, but only if the second filing cannot succeed.
    /// </remarks>
    public required string SourceRef { get; init; }

    /// <summary>This reply's own message id, bare, when it carried one.</summary>
    public string? MessageId { get; init; }

    /// <summary>Who it came from.</summary>
    public required string FromAddress { get; init; }

    /// <summary>Its subject line, when it had one.</summary>
    public string? Subject { get; init; }

    /// <summary>How it was tied to the attempt.</summary>
    public required ReplyMatch MatchedBy { get; init; }

    /// <summary>When the company sent it.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>When this instance read it.</summary>
    public DateTimeOffset FiledAt { get; init; }
}
