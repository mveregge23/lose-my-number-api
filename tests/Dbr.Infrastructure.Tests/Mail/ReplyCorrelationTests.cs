// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Infrastructure.Mail;
using Microsoft.Extensions.Options;

namespace Dbr.Infrastructure.Tests.Mail;

/// <summary>
/// What an arriving message offers as evidence of which attempt it answers.
/// </summary>
public class ReplyCorrelationTests
{
    private const string Domain = "answers.test";

    private static readonly Guid Job = Guid.Parse("0f8b1d42-6a19-4d5e-9f2b-3c7a58e1d604");

    private static readonly IJobMailboxes Mailboxes =
        new JobMailboxes(Options.Create(new MailOptions { Domain = Domain }));

    [Fact]
    public void A_message_to_an_attempts_own_address_names_that_attempt()
    {
        var evidence = ReplyCorrelation.Evidence(
            Message(to: [JobMailbox.For(Job, Domain).Address]),
            Mailboxes);

        Assert.Equal(Job, evidence.JobId);
    }

    /// <summary>
    /// The address counts wherever in the recipients it appears.
    /// </summary>
    /// <remarks>
    /// A desk that answers a demand and copies its own ticket queue puts our address
    /// second, and the message is no less an answer for it.
    /// </remarks>
    [Fact]
    public void An_attempt_named_among_several_recipients_is_still_named()
    {
        var evidence = ReplyCorrelation.Evidence(
            Message(to: ["support@company.test", JobMailbox.For(Job, Domain).Address]),
            Mailboxes);

        Assert.Equal(Job, evidence.JobId);
    }

    [Fact]
    public void A_message_to_somebody_elses_domain_names_no_attempt()
    {
        var evidence = ReplyCorrelation.Evidence(
            Message(to: [$"{JobMailbox.Prefix}{Job:D}@elsewhere.test"]),
            Mailboxes);

        Assert.Null(evidence.JobId);
    }

    /// <summary>
    /// The nearest quoted id comes first, and the thread is read from its end.
    /// </summary>
    /// <remarks>
    /// The order is the whole of what this produces, and it is what decides which attempt a
    /// long ticket thread resolves to. Read forwards, a company that has been written to
    /// twice about the same listing would have its second reply filed against the first
    /// demand.
    /// </remarks>
    [Fact]
    public void What_is_answered_is_offered_before_the_rest_of_the_thread()
    {
        var evidence = ReplyCorrelation.Evidence(
            Message(
                to: ["someone@company.test"],
                inReplyTo: "second-demand@relay.test",
                references: ["ticket-opened@company.test", "first-demand@relay.test"]),
            Mailboxes);

        Assert.Equal(
            ["second-demand@relay.test", "first-demand@relay.test", "ticket-opened@company.test"],
            evidence.QuotedMessageIds);
    }

    [Fact]
    public void An_id_quoted_twice_is_offered_once()
    {
        var evidence = ReplyCorrelation.Evidence(
            Message(
                to: ["someone@company.test"],
                inReplyTo: "demand@relay.test",
                references: ["demand@relay.test"]),
            Mailboxes);

        Assert.Equal(["demand@relay.test"], evidence.QuotedMessageIds);
    }

    /// <summary>
    /// A message with neither key offers nothing, and says so.
    /// </summary>
    /// <remarks>
    /// The ordinary case rather than the exceptional one: a mailbox demands go out from
    /// receives everything anybody sends it, and the caller skips the lookup entirely when
    /// there is nothing to look up.
    /// </remarks>
    [Fact]
    public void Ordinary_mail_offers_nothing_to_resolve()
    {
        var evidence = ReplyCorrelation.Evidence(Message(to: ["hello@answers.test"]), Mailboxes);

        Assert.True(evidence.IsEmpty);
    }

    private static InboundMessage Message(
        IReadOnlyList<string> to,
        string? inReplyTo = null,
        IReadOnlyList<string>? references = null) =>
        new()
        {
            SourceRef = "1-1",
            From = "privacy@company.test",
            To = to,
            InReplyTo = inReplyTo,
            References = references ?? [],
            ReceivedAt = DateTimeOffset.UnixEpoch,
        };
}
