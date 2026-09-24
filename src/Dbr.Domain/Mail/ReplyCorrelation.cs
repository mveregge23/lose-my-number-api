// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// How a reply was tied to the attempt it answers.
/// </summary>
/// <remarks>
/// Kept on every filed reply, and not for diagnostics. The two ways differ in what they
/// prove about the deployment rather than about the message: a reply resolved by the
/// address it was sent to means this instance's per-attempt return address survived the
/// path out and back, and one resolved by thread headers means it may not have. That is a
/// fact about the relay, and the only place it is observable is here.
/// </remarks>
public enum ReplyMatch
{
    /// <summary>It was addressed to the mailbox one attempt speaks from.</summary>
    MailboxAddress,

    /// <summary>It quoted the id an attempt sent its demand under.</summary>
    ThreadHeaders,
}

/// <summary>
/// What a message offers as evidence of which attempt it answers.
/// </summary>
/// <param name="JobId">The attempt a recipient named, when one did.</param>
/// <param name="QuotedMessageIds">
/// The ids the message claims to be answering, nearest first — <c>In-Reply-To</c> before
/// <c>References</c>, and within <c>References</c> the end of the thread before its start,
/// because the most recent message in a thread is the one most likely to be ours.
/// </param>
public sealed record ReplyEvidence(Guid? JobId, IReadOnlyList<string> QuotedMessageIds)
{
    /// <summary>Whether this message offers anything to resolve at all.</summary>
    public bool IsEmpty => JobId is null && QuotedMessageIds.Count == 0;
}

/// <summary>
/// Reads a message for the things that could name an attempt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two mechanisms, and the second is not a fallback in the ordinary sense.</b> The
/// per-attempt return address is the better key — it is unambiguous and does not depend on
/// the company's mail client — and it is also the one that most often never leaves. A
/// relay that only permits its authenticated account as a sender rewrites the address
/// silently, so a demand believed to have been sent from an attempt's own mailbox arrives
/// at the company as having come from the account, and the reply comes back naming
/// nothing. That is not hypothetical: it is what the first demand this system sent for
/// real did, through a consumer mail account.
/// </para>
/// <para>
/// So both are read on every message, the address first because it is exact, and what
/// actually resolved is recorded. Neither is trusted on its own: knowing an address or an
/// id is not authority to do anything, since both went out to a company in the open. They
/// are correlation, and whoever acts on a resolved reply is expected to check that the
/// attempt was waiting for an answer.
/// </para>
/// </remarks>
public static class ReplyCorrelation
{
    /// <summary>What this message offers, in the order it should be tried.</summary>
    public static ReplyEvidence Evidence(InboundMessage message, IJobMailboxes mailboxes)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mailboxes);

        Guid? job = null;

        foreach (var recipient in message.To)
        {
            if (mailboxes.TryResolve(recipient, out var named))
            {
                job = named;
                break;
            }
        }

        var quoted = new List<string>();

        if (message.InReplyTo is { } answering)
        {
            quoted.Add(answering);
        }

        // Backwards, because the thread's last message is the one this answers and the
        // first is whatever started it — which on a long ticket thread is as likely to be
        // the company's own auto-reply as it is to be our demand.
        for (var i = message.References.Count - 1; i >= 0; i--)
        {
            var reference = message.References[i];
            if (!quoted.Contains(reference, StringComparer.Ordinal))
            {
                quoted.Add(reference);
            }
        }

        return new ReplyEvidence(job, quoted);
    }
}
