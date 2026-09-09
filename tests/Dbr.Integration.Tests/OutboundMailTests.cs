// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Infrastructure.Mail;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dbr.Integration.Tests;

/// <summary>
/// The sender against a server that answers, rather than against a double that agrees.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about mail is checked with a stand-in for <see cref="IMailSender"/>,
/// which is right for testing what a connector does with an outcome and useless for testing
/// how an outcome is arrived at. The three claims here can only be made against something
/// speaking SMTP: that a message leaves in the shape it was built, that the credential is
/// actually presented, and above all <b>that a refusal is classified by what the relay
/// said</b> — which is what decides whether a demand spends another of its attempts.
/// </para>
/// <para>
/// No database and no containers. The sender talks to a socket, so this is the one part of
/// the integration tier that needs neither.
/// </para>
/// </remarks>
public class OutboundMailTests
{
    private const string Domain = "removals.example.org";

    private static OutboundMessage Demand(Guid jobId) =>
        new(
            JobMailbox.For(jobId, Domain),
            "privacy@example.com",
            "Request to delete personal information",
            "Name: Alex Rivera\r\nCity: Springfield\r\n");

    private static SmtpMailSender Sender(FixtureSmtpServer server, bool requireTls = false) =>
        new(
            Options.Create(new MailOptions
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "dbr",
                Password = "secret",
                Domain = Domain,
                RequireTls = requireTls,
            }),
            NullLogger<SmtpMailSender>.Instance);

    [Fact]
    public async Task A_demand_arrives_addressed_from_the_job_that_made_it()
    {
        await using var server = FixtureSmtpServer.Start();
        var jobId = Guid.NewGuid();

        var receipt = await Sender(server)
            .SendAsync(Demand(jobId), TestContext.Current.CancellationToken);

        var sent = Assert.Single(server.Messages);

        // The envelope, which is what a relay routes on.
        Assert.Equal($"removals-{jobId:D}@{Domain}", sent.From);
        Assert.Equal("privacy@example.com", Assert.Single(sent.Recipients));

        // And the headers, which are what a person reads.
        Assert.Equal($"removals-{jobId:D}@{Domain}", sent.Header("From"));
        Assert.Equal("Request to delete personal information", sent.Header("Subject"));
        Assert.Contains("Alex Rivera", sent.Body, StringComparison.Ordinal);

        // The receipt names the message that was actually sent, which is the only thing
        // that makes it worth keeping — a company asked to look a demand up is given an id
        // its own server saw.
        Assert.Equal($"<{receipt.MessageId}>", sent.Header("Message-Id"));
    }

    [Fact]
    public async Task There_is_no_second_recipient_and_no_reply_to()
    {
        // Both would be a copy of somebody's identity going somewhere nobody chose, and
        // neither is visible in a test that only reads the outcome.
        await using var server = FixtureSmtpServer.Start();

        await Sender(server).SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken);

        var sent = Assert.Single(server.Messages);

        Assert.Single(sent.Recipients);
        Assert.Null(sent.Header("Cc"));
        Assert.Null(sent.Header("Bcc"));
        Assert.Null(sent.Header("Reply-To"));
    }

    [Fact]
    public async Task The_credential_is_presented()
    {
        await using var server = FixtureSmtpServer.Start();

        await Sender(server).SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal("dbr", server.PresentedUser);
    }

    [Theory]
    [InlineData(421)]
    [InlineData(450)]
    [InlineData(451)]
    public async Task A_relay_asking_us_to_come_back_is_worth_another_attempt(int code)
    {
        // The claim the whole fixture exists for. A 4xx is a request to try later, and
        // reporting it as permanent spends a demand's budget on an answer that would have
        // changed.
        await using var server = FixtureSmtpServer.Start();
        server.RefuseWith = code;

        var problem = await Assert.ThrowsAsync<MailDeliveryException>(() =>
            Sender(server).SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.True(problem.Transient);
        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(550)]
    [InlineData(552)]
    [InlineData(554)]
    public async Task A_relay_refusing_outright_is_not(int code)
    {
        // And the mirror. Retrying a 5xx sends the same message to the same refusal, which
        // is persistence spent on nothing.
        await using var server = FixtureSmtpServer.Start();
        server.RefuseWith = code;

        var problem = await Assert.ThrowsAsync<MailDeliveryException>(() =>
            Sender(server).SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.False(problem.Transient);
    }

    [Fact]
    public async Task A_refused_demand_leaves_nothing_behind()
    {
        await using var server = FixtureSmtpServer.Start();
        server.RefuseWith = 550;

        await Assert.ThrowsAsync<MailDeliveryException>(() =>
            Sender(server).SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task A_relay_that_cannot_be_reached_is_worth_another_attempt()
    {
        // Nothing was refused; the conversation never happened. Distinct from a refusal
        // because the relay restarting mid-deploy is the case this actually catches.
        await using var server = FixtureSmtpServer.Start();
        var port = server.Port;
        await server.DisposeAsync();

        var sender = new SmtpMailSender(
            Options.Create(new MailOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Username = "dbr",
                Password = "secret",
                Domain = Domain,
            }),
            NullLogger<SmtpMailSender>.Instance);

        var problem = await Assert.ThrowsAsync<MailDeliveryException>(() =>
            sender.SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.True(problem.Transient);
    }

    [Fact]
    public async Task Requiring_encryption_refuses_a_relay_that_offers_none()
    {
        // The setting has to mean what it says. A relay reached across a network without
        // TLS hands the credential and the body of every demand — a name and a home address
        // — to anything on the path, so "require" must fail rather than fall back.
        await using var server = FixtureSmtpServer.Start();

        await Assert.ThrowsAsync<MailDeliveryException>(() =>
            Sender(server, requireTls: true)
                .SendAsync(Demand(Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task Cancelling_is_not_a_delivery_outcome()
    {
        // A process shutting down must not spend one of a demand's attempts. The
        // cancellation is left to escape rather than being reported as a failure.
        await using var server = FixtureSmtpServer.Start();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sender(server).SendAsync(Demand(Guid.NewGuid()), cancelled.Token));
    }
}
