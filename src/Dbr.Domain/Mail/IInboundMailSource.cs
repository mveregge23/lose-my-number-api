// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// A message that arrived at whatever address this instance's demands go out from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers and no body.</b> Everything here is what decides which attempt a message
/// answers; what a company actually wrote is not read yet, and a source that never
/// downloads it cannot leak it. Reading the prose is what tells a confirmation from a
/// refusal, and it arrives with the classification that needs it — together with the
/// decision about where, if anywhere, it is kept.
/// </para>
/// <para>
/// <b>Message ids are bare.</b> A header writes one inside angle brackets and the id is
/// what sits between them, which is the spelling an attempt already stores the id it sent
/// under. Normalizing at the edge means one place knows about brackets, rather than every
/// later comparison having to try both spellings — and getting that wrong produces a reply
/// that matches nothing, which is indistinguishable from a company that never answered.
/// </para>
/// </remarks>
public sealed record InboundMessage
{
    /// <summary>
    /// What the source calls this message, so it can be told the message is dealt with.
    /// </summary>
    /// <remarks>
    /// The source's own handle rather than the message id: a mailbox names messages by
    /// their place in a folder, a webhook by a delivery id, and neither is obliged to have
    /// looked at the headers. A message that arrives without a <c>Message-ID</c> at all
    /// still has to be acknowledged, or it arrives again on every pass forever.
    /// </remarks>
    public required string SourceRef { get; init; }

    /// <summary>Who sent it, as the message carries it.</summary>
    public required string From { get; init; }

    /// <summary>
    /// Everyone it was addressed to.
    /// </summary>
    /// <remarks>
    /// A list rather than one address, because the recipient that names an attempt may not
    /// be the first one — a company replying to several addresses at once, or a desk that
    /// copies itself, still answers the demand.
    /// </remarks>
    public required IReadOnlyList<string> To { get; init; }

    /// <summary>This message's own id, bare, when it carried one.</summary>
    public string? MessageId { get; init; }

    /// <summary>The id this message says it answers, bare, when it says so.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>
    /// Every id in the thread this message claims to be part of, bare and in order.
    /// </summary>
    /// <remarks>
    /// Read as well as <see cref="InReplyTo"/> because a desk that answers through a
    /// ticketing system frequently keeps the thread in <c>References</c> while pointing
    /// <c>In-Reply-To</c> at its own last message. Both are the sender's claim about what
    /// this answers rather than proof of it, which is why resolving one to an attempt is a
    /// lookup and not a trust decision.
    /// </remarks>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The subject line, when there was one.</summary>
    public string? Subject { get; init; }

    /// <summary>When it arrived.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Names the message and withholds the address it came from having been read.
    /// </summary>
    /// <remarks>
    /// A subject line is written by the company rather than by us and can carry anything
    /// it likes, a person's name included — several of the ticketing systems that answer
    /// these demands quote the request back in it. So it stays off the line a log
    /// interpolates, the same rule an outbound message follows for its body.
    /// </remarks>
    public override string ToString() =>
        $"InboundMessage {{ From = {From}, SourceRef = {SourceRef}, [withheld] }}";
}

/// <summary>
/// Where replies to this instance's demands are read from.
/// </summary>
/// <remarks>
/// <para>
/// A port with more than one thing behind it, and the two have nothing in common but this
/// shape. A mailbox is polled over IMAP and needs no domain, no MX and no inbound port,
/// which is what lets a deployment read its own replies on the day it is set up. A
/// provider that owns the domain pushes them instead. Both produce the same message.
/// </para>
/// <para>
/// <b>Fetching does not consume.</b> A message stays fetchable until it is acknowledged,
/// so a process that crashes between reading a reply and filing it reads the same reply
/// again rather than losing it. The cost is that filing has to tolerate seeing one twice,
/// which is a duplicate a unique constraint can refuse — and the alternative cost is a
/// company's only answer disappearing into a restart.
/// </para>
/// </remarks>
public interface IInboundMailSource
{
    /// <summary>Whatever has arrived and not yet been acknowledged, oldest first.</summary>
    Task<IReadOnlyList<InboundMessage>> FetchAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Says this message has been dealt with and need not be offered again.</summary>
    Task AcknowledgeAsync(InboundMessage message, CancellationToken cancellationToken);
}
