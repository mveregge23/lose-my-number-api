// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// One message, in the only terms anything here needs to describe one.
/// </summary>
/// <remarks>
/// <para>
/// Plain text and nothing else. A demand is a legal document read by whoever staffs a
/// company's opt-out mailbox, and the two things HTML would add are a richer way to say the
/// same sentences and a set of ways to leak — remote images that report when a message was
/// opened and from where, and a body whose rendered text differs from its source. Neither
/// belongs on a message sent on somebody else's behalf.
/// </para>
/// <para>
/// <b>No attachments, and no recipient list.</b> One address, because a demand is made of
/// one company and a second recipient would be a copy of somebody's identity sent somewhere
/// nobody chose. Attachments are what an identity document would arrive as, and those never
/// leave the vault on this path — a company that requires one is a
/// <see cref="Connectors.HumanInputKind.IdentityDocument"/> ask, resolved by the person
/// themselves.
/// </para>
/// </remarks>
/// <param name="From">
/// The address the answer comes back to, which is the job's own. It is what ties a reply to
/// an attempt, so it is the sender rather than a header alongside it.
/// </param>
/// <param name="To">The company's opt-out mailbox, as its catalog entry gives it.</param>
/// <param name="Subject">
/// The one-line summary. Written to say what the message is without saying who it is about:
/// it travels in the clear through every relay between here and the company, and it is the
/// part that shows up in a notification on somebody's phone.
/// </param>
/// <param name="Body">The demand itself.</param>
public sealed record OutboundMessage(JobMailbox From, string To, string Subject, string Body)
{
    /// <summary>
    /// Names the message and withholds the half of it that is somebody's identity.
    /// </summary>
    /// <remarks>
    /// A record prints every member it has, and the body of a demand is a person's name and
    /// address written out in prose — the most quotable shape that data ever takes here, and
    /// one interpolation away from a log line. What is left says which job wrote to which
    /// company, which is what somebody following a delivery failure actually needs.
    /// </remarks>
    public override string ToString() =>
        $"OutboundMessage {{ From = {From.Address}, To = {To}, [withheld] }}";
}

/// <summary>
/// What the relay said when it took the message.
/// </summary>
/// <remarks>
/// <para>
/// A message id and not a delivery confirmation, and the difference is the whole reason
/// this is a named type rather than a <see langword="bool"/>. Accepting a message means the
/// relay has taken responsibility for delivering it, not that a company received it and
/// certainly not that a company read it — those answers arrive later, as a reply or as
/// silence when the deadline expires.
/// </para>
/// <para>
/// It is worth keeping anyway, and for a specific moment: a company that later says it
/// never received a demand can be answered with the id its own server acknowledged.
/// </para>
/// </remarks>
/// <param name="MessageId">
/// The <c>Message-ID</c> the message carried, without the angle brackets it wears in a
/// header.
/// </param>
public sealed record MailReceipt(string MessageId);

/// <summary>
/// The message was not handed over.
/// </summary>
/// <remarks>
/// <see cref="Transient"/> is the part worth carrying, because the caller cannot work it
/// out afterwards. A relay refusing a message temporarily and a relay refusing it outright
/// are the same silence from the outside, and they want opposite handling: one is worth
/// another attempt from the demand's budget, and the other spends the budget on an answer
/// that will not change. SMTP already draws that line — a 4xx is a request to come back and
/// a 5xx is a refusal — and this preserves it rather than making the caller re-derive it
/// from a message string.
/// </remarks>
public sealed class MailDeliveryException : Exception
{
    public MailDeliveryException(string message, bool transient, Exception? innerException = null)
        : base(message, innerException)
    {
        Transient = transient;
    }

    /// <summary>Whether trying again could plausibly go differently.</summary>
    public bool Transient { get; }
}

/// <summary>
/// Hands a message to the relay this instance sends through.
/// </summary>
/// <remarks>
/// <para>
/// The seam the design promises for anything with a vendor behind it, pointed at mail. What
/// sits on the other side is an SMTP relay the operator runs, and the reason this interface
/// exists rather than a client being newed up where it is needed is that mail is the one
/// dependency here with a strong pull toward a hosted API — a transactional-email SaaS is
/// easier to set up than a relay, and it would hold a standing copy of every tenant's
/// correspondence with every company they have ever asked. Keeping the seam means that
/// choice stays an operator's registration rather than something compiled in.
/// </para>
/// <para>
/// <b>It throws, and that is deliberate.</b> The connector contract forbids exceptions
/// escaping a connector, which is exactly why this one is allowed to raise them: a
/// connector catching <see cref="MailDeliveryException"/> and turning it into a failure with
/// a reason is the translation the contract asks for, and an interface returning a result
/// here would leave every caller to remember to check it.
/// </para>
/// </remarks>
public interface IMailSender
{
    /// <summary>Sends one message, or says why it could not.</summary>
    /// <exception cref="MailDeliveryException">The relay did not take the message.</exception>
    Task<MailReceipt> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}
