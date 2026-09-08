// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Mail;
using Dbr.Domain.Profiles;
using Dbr.Domain.Recipes;

namespace Dbr.Removals.Tests;

/// <summary>What the engine does with a demand, and what it refuses to do.</summary>
public class TemplatedEmailConnectorTests
{
    private static readonly Guid BrokerId = Guid.Parse("2f6b1c48-9d3a-4e57-b8a1-0c5e7f9d24b3");
    private static readonly DateTimeOffset Deadline = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ProfileIdentityFields Alex = new(
        ["Alex Rivera"],
        [new ProfileAddress(Guid.NewGuid(), "1 Main St", null, "Springfield", "CA", "90210", "US")],
        [new ProfileContact(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.com")],
        null);

    [Fact]
    public async Task A_demand_goes_to_the_company_from_the_job_that_made_it()
    {
        var sender = new RecordingSender();
        var connector = Build(sender);
        var context = Context();

        var result = await connector.ExecuteAsync(context, TestContext.Current.CancellationToken);

        var sent = Assert.Single(sender.Sent);

        // The mailbox is the recipe's local part on the company's own domain, and the sender
        // is the job's address — which is what a reply resolves back to.
        Assert.Equal("privacy@example.com", sent.To);
        Assert.Equal(context.JobId, sent.From.JobId);
        Assert.Contains(context.JobId.ToString("D"), sent.From.Address, StringComparison.Ordinal);

        // The demand is in and the clock is running. Not Success: whether the listing is
        // actually gone is a question only a verification scan answers.
        var awaiting = Assert.IsType<ConnectorResult.AwaitingBrokerResponse>(result);
        Assert.Equal(Deadline, awaiting.Deadline);
    }

    [Fact]
    public async Task The_identity_arrives_as_it_is_held_and_not_escaped()
    {
        // The failure this catches only shows up for the people whose names contain
        // punctuation, which is exactly the kind that ships.
        var sender = new RecordingSender();
        var connector = Build(sender);

        var ampersand = Alex with { Names = ["Alex Rivera & Co"] };

        await connector.ExecuteAsync(
            Context(identity: ampersand),
            TestContext.Current.CancellationToken);

        Assert.Contains("Alex Rivera & Co", sender.Sent[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("%26", sender.Sent[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wording_that_does_not_exist_is_not_improvised()
    {
        // A demand sent under the wrong act claims an obligation in somebody's name that
        // nothing established, so the absence of wording is a refusal rather than a fallback.
        var connector = Build(new RecordingSender());

        var result = await connector.ExecuteAsync(
            Context(statute: "CTDPA"),
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<ConnectorResult.Failed>(result);

        Assert.Equal(ConnectorFailureReason.Unsupported, failed.Reason);
        Assert.False(failed.Retryable);
        Assert.Contains("CTDPA", failed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_sent_when_there_is_no_wording()
    {
        var sender = new RecordingSender();
        var connector = Build(sender);

        await connector.ExecuteAsync(Context(statute: "CTDPA"), TestContext.Current.CancellationToken);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_profile_missing_what_the_wording_needs_cannot_make_the_demand()
    {
        var sender = new RecordingSender();
        var connector = Build(sender);

        var noEmail = Alex with { Contacts = [] };

        var result = await connector.ExecuteAsync(
            Context(identity: noEmail),
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<ConnectorResult.Failed>(result);

        // Unsupported and not retryable: another attempt against the same profile writes the
        // same gap.
        Assert.Equal(ConnectorFailureReason.Unsupported, failed.Reason);
        Assert.False(failed.Retryable);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_relay_that_refuses_temporarily_is_worth_another_attempt()
    {
        var connector = Build(new ThrowingSender(transient: true));

        var result = await connector.ExecuteAsync(Context(), TestContext.Current.CancellationToken);

        var failed = Assert.IsType<ConnectorResult.Failed>(result);

        Assert.Equal(ConnectorFailureReason.Transient, failed.Reason);
        Assert.True(failed.Retryable);
    }

    [Fact]
    public async Task A_relay_that_refuses_outright_is_not()
    {
        // The relay's own answer rather than a guess: a 4xx asks us to come back and a 5xx
        // does not, and spending the demand's budget on the second is spending it on nothing.
        var connector = Build(new ThrowingSender(transient: false));

        var result = await connector.ExecuteAsync(Context(), TestContext.Current.CancellationToken);

        Assert.False(Assert.IsType<ConnectorResult.Failed>(result).Retryable);
    }

    [Fact]
    public async Task An_exception_never_escapes()
    {
        // The contract's rule, and the reason the sender is allowed to throw at all.
        var connector = Build(new ThrowingSender(transient: true));

        var result = await connector.ExecuteAsync(Context(), TestContext.Current.CancellationToken);

        Assert.IsType<ConnectorResult.Failed>(result);
    }

    [Fact]
    public void What_it_declares_is_what_its_wording_writes()
    {
        var connector = Build(new RecordingSender());

        Assert.Equal(RemovalMethod.Email, connector.Capabilities.Method);
        Assert.Equal(ConnectorKind.Recipe, connector.Capabilities.Kind);

        // Derived from the documents rather than written out beside them, so the declaration
        // cannot disagree with what the wording actually fills in.
        Assert.Contains(IdentityField.Names, connector.Capabilities.RequiredFields);
        Assert.Contains(IdentityField.Addresses, connector.Capabilities.RequiredFields);
        Assert.Contains(IdentityField.Contacts, connector.Capabilities.RequiredFields);

        // Nothing in the wording mentions one, so there is no moment at which this connector
        // could cause a date of birth to be decrypted.
        Assert.DoesNotContain(IdentityField.DateOfBirth, connector.Capabilities.RequiredFields);
    }

    [Fact]
    public async Task The_contract_accepts_what_it_produces()
    {
        // The two halves checked against each other rather than separately: the handler runs
        // this exact check on the way in and on the way out, and a connector that passed its
        // own tests and failed that one would be caught here rather than in production.
        var sender = new RecordingSender();
        var connector = Build(sender);
        var context = Context();

        Assert.Null(ConnectorContract.Refuse(connector.Capabilities, context));

        var result = await connector.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Null(ConnectorContract.Refuse(connector.Capabilities, result));
    }

    private static TemplatedEmailConnector Build(IMailSender sender) =>
        new(
            new EmailRecipe(BrokerId, "privacy"),
            [Template()],
            sender,
            new FixedMailboxes());

    private static DemandTemplate Template()
    {
        var subject = RecipeTemplate.TryParse("Request to delete personal information", out _)!;
        var body = RecipeTemplate.TryParse(
            "Name: {{names.full}}\nCity: {{addresses.first.city}}\nEmail: {{contacts.email}}",
            out _)!;

        return new DemandTemplate(DemandTemplateKey.For("CCPA", LegalRequestType.Delete), subject, body);
    }

    private static ConnectorContext Context(
        ProfileIdentityFields? identity = null,
        string? statute = "CCPA") =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ConnectorTarget(BrokerId, "example.com", RemovalMethod.Email),
            new ConnectorDemand(
                LegalRequestType.Delete,
                DeadlineSource.Statutory,
                Deadline,
                statute,
                new Uri("https://example.test/ccpa")),
            identity ?? Alex,
            null,
            null,
            1);

    private sealed class FixedMailboxes : IJobMailboxes
    {
        public JobMailbox For(Guid jobId) => JobMailbox.For(jobId, "removals.example.org");

        public bool TryResolve(string? address, out Guid jobId) =>
            JobMailbox.TryResolve(address, "removals.example.org", out jobId);
    }

    private sealed class RecordingSender : IMailSender
    {
        public List<OutboundMessage> Sent { get; } = [];

        public Task<MailReceipt> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);

            return Task.FromResult(new MailReceipt("test@example.test"));
        }
    }

    private sealed class ThrowingSender(bool transient) : IMailSender
    {
        public Task<MailReceipt> SendAsync(OutboundMessage message, CancellationToken cancellationToken) =>
            throw new MailDeliveryException("the relay said no", transient);
    }
}
