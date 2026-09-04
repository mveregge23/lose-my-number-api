// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Profiles;

namespace Dbr.Infrastructure.Tests.Connectors;

/// <summary>
/// The rules a connector and its caller have to keep, and what each one refuses.
/// </summary>
/// <remarks>
/// Every case here is something a contributed connector or a dispatch bug could do by
/// accident. Three matter more than the rest: a context carrying more of an identity than
/// was asked for means something has already been decrypted that should not have been; a
/// connector resolved for the wrong kind of company burns an entry's retries on a fault that
/// is not the company's; and a refusal marked retryable would send a demand back to somebody
/// who has already answered it. None of the three would fail a test that only checked the
/// shape of the types.
/// </remarks>
public class ConnectorContractTests
{
    private static readonly Uri Statute = new("https://oag.ca.gov/privacy/ccpa");

    private static ConnectorCapabilities Needs(params IdentityField[] fields) =>
        new(ConnectorKind.Recipe, RemovalMethod.Email, fields.ToHashSet());

    private static ProfileIdentityFields Identity(
        IReadOnlyList<string>? names = null,
        IReadOnlyList<ProfileAddress>? addresses = null,
        IReadOnlyList<ProfileContact>? contacts = null,
        DateOnly? dateOfBirth = null) =>
        new(names ?? [], addresses ?? [], contacts ?? [], dateOfBirth);

    private static ConnectorDemand Demand() =>
        new(
            LegalRequestType.Delete,
            DeadlineSource.Statutory,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "CCPA",
            Statute);

    private static ConnectorContext Context(ProfileIdentityFields? identity = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ConnectorTarget(Guid.NewGuid(), "example-broker.test", RemovalMethod.Email),
            Demand(),
            identity ?? Identity(names: ["Alex Whitfield"]),
            null,
            null,
            1);

    private static ProfileAddress AnAddress() =>
        new(Guid.NewGuid(), "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US");

    private static ProfileContact AContact() =>
        new(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test");

    [Fact]
    public void A_context_that_carries_what_was_declared_is_allowed()
    {
        Assert.Null(ConnectorContract.Refuse(Needs(IdentityField.Names), Context()));
    }

    /// <summary>
    /// A connector that names no part of an identity has nobody to speak for.
    /// </summary>
    /// <remarks>
    /// The context here releases nothing, and that is the point rather than tidiness. An
    /// identity carrying anything at all is already refused by the over-release rule — every
    /// group it holds is one the connector never declared — so a context with a name in it
    /// would pass this test with the rule below deleted, and the assertion would be watching
    /// the wrong thing entirely.
    /// </remarks>
    [Fact]
    public void A_connector_that_needs_no_field_is_refused()
    {
        var refusal = ConnectorContract.Refuse(
            new ConnectorCapabilities(ConnectorKind.Code, RemovalMethod.Email, new HashSet<IdentityField>()),
            Context(Identity()));

        Assert.NotNull(refusal);
        Assert.Contains("at least one field", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_naming_no_job_is_refused()
    {
        var context = Context() with { JobId = Guid.Empty };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_context_naming_no_request_is_refused()
    {
        var context = Context() with { RemovalRequestId = Guid.Empty };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_context_naming_no_broker_is_refused()
    {
        var context = Context() with
        {
            Broker = new ConnectorTarget(Guid.Empty, "example-broker.test", RemovalMethod.Email),
        };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_context_with_no_company_to_address_is_refused(string domain)
    {
        var context = Context() with
        {
            Broker = new ConnectorTarget(Guid.NewGuid(), domain, RemovalMethod.Email),
        };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Attempts_are_counted_from_one(int attempt)
    {
        var context = Context() with { AttemptNumber = attempt };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    /// <summary>
    /// A connector pointed at a company that accepts demands some other way.
    /// </summary>
    /// <remarks>
    /// Worth its own rule because of how it fails otherwise: a form connector handed a
    /// mailbox-only company finds no form and reports that the page changed, which is the
    /// one failure that raises a catalog-maintenance signal — so a dispatch bug would arrive
    /// looking like a company that redesigned its site, and would spend that entry's retries
    /// proving it.
    /// </remarks>
    [Fact]
    public void A_connector_that_acts_the_wrong_way_for_this_company_is_refused()
    {
        var capabilities = new ConnectorCapabilities(
            ConnectorKind.Recipe,
            RemovalMethod.WebForm,
            new HashSet<IdentityField> { IdentityField.Names });

        var refusal = ConnectorContract.Refuse(capabilities, Context());

        Assert.NotNull(refusal);
        Assert.Contains("web form", refusal, StringComparison.Ordinal);
        Assert.Contains("email", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The release handed over something the connector never asked for.
    /// </summary>
    /// <remarks>
    /// Refused rather than trimmed. By the time this is visible the value has already left
    /// the vault, so dropping it here would hide a fault in whatever built the release while
    /// doing nothing about the decryption that already happened.
    /// </remarks>
    [Fact]
    public void A_context_carrying_an_undeclared_field_is_refused()
    {
        var context = Context(Identity(names: ["Alex Whitfield"], dateOfBirth: new DateOnly(1985, 4, 17)));

        var refusal = ConnectorContract.Refuse(Needs(IdentityField.Names), context);

        Assert.NotNull(refusal);
        Assert.Contains("date of birth", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_carrying_an_undeclared_address_is_refused()
    {
        var context = Context(Identity(names: ["Alex Whitfield"], addresses: [AnAddress()]));

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_context_carrying_an_undeclared_contact_is_refused()
    {
        var context = Context(Identity(names: ["Alex Whitfield"], contacts: [AContact()]));

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    /// <summary>
    /// A group that arrived empty released nothing, so there is nothing to refuse.
    /// </summary>
    /// <remarks>
    /// Asserted rather than left implied, so that nobody later reads the over-release rule
    /// as stricter than it is. A profile with no addresses on file and a release that
    /// withheld them are indistinguishable here, and treating the pair as a fault would
    /// refuse every ordinary attempt for somebody who has only ever given a name.
    /// </remarks>
    [Fact]
    public void A_declared_field_that_arrived_empty_is_allowed()
    {
        var context = Context(Identity(names: ["Alex Whitfield"]));

        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names, IdentityField.Addresses),
            context));
    }

    [Fact]
    public void A_demand_claiming_a_statute_without_naming_one_is_refused()
    {
        var context = Context() with { Demand = Demand() with { StatuteCode = null } };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_demand_naming_a_statute_nobody_can_read_is_refused()
    {
        var context = Context() with { Demand = Demand() with { StatuteCitation = null } };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    /// <summary>
    /// A courtesy deadline dressed up as an obligation.
    /// </summary>
    /// <remarks>
    /// The demand goes out over somebody's name, so a statute cited when none was found to
    /// govern is not an internal inconsistency — it is this service asserting a legal
    /// obligation to a company on a person's behalf that nothing established.
    /// </remarks>
    [Fact]
    public void A_courtesy_deadline_that_cites_a_statute_anyway_is_refused()
    {
        var context = Context() with
        {
            Demand = Demand() with { Source = DeadlineSource.OperationalDefault },
        };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_courtesy_deadline_citing_nothing_is_allowed()
    {
        var context = Context() with
        {
            Demand = Demand() with
            {
                Source = DeadlineSource.OperationalDefault,
                StatuteCode = null,
                StatuteCitation = null,
            },
        };

        Assert.Null(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Theory]
    [InlineData("file:///etc/statutes")]
    [InlineData("javascript:alert(1)")]
    public void A_citation_that_is_not_a_web_address_is_refused(string citation)
    {
        var context = Context() with
        {
            Demand = Demand() with { StatuteCitation = new Uri(citation) },
        };

        Assert.NotNull(ConnectorContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void An_ordinary_outcome_is_believed()
    {
        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.Success("TICKET-4471", null)));

        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.AlreadyClear()));

        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.AwaitingBrokerResponse(
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_that_says_nothing_about_itself_is_refused(string detail)
    {
        var refusal = ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.Failed(ConnectorFailureReason.Transient, detail, true));

        Assert.NotNull(refusal);
    }

    /// <summary>
    /// A company that said no, asked again.
    /// </summary>
    /// <remarks>
    /// The one failure reason where retrying is not merely wasteful. A refusal is a
    /// judgement the company is entitled to make, and sending the same demand back at it is
    /// the behaviour that gets an instance blocked — and gets a person's name in front of
    /// that company a second time for nothing.
    /// </remarks>
    [Fact]
    public void A_refusal_that_asks_to_be_retried_is_refused()
    {
        var refusal = ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.Failed(
                ConnectorFailureReason.Rejected,
                "the form answered: we do not hold data for this person",
                true));

        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_refusal_that_accepts_it_is_believed()
    {
        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.Failed(
                ConnectorFailureReason.Rejected,
                "the form answered: we do not hold data for this person",
                false)));
    }

    /// <summary>
    /// Only the reason that means "they answered" is held to this.
    /// </summary>
    /// <remarks>
    /// Asserted so the rule is not later read as "failures are not retryable". A timeout
    /// asking for another attempt is the ordinary case and the whole reason the flag exists.
    /// </remarks>
    [Fact]
    public void Every_other_failure_may_still_ask_for_another_attempt()
    {
        foreach (var reason in Enum.GetValues<ConnectorFailureReason>())
        {
            if (reason == ConnectorFailureReason.Rejected)
            {
                continue;
            }

            Assert.Null(ConnectorContract.Refuse(
                Needs(IdentityField.Names),
                new ConnectorResult.Failed(reason, "the connection was reset", true)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_stop_that_does_not_say_what_is_needed_is_refused(string instructions)
    {
        var refusal = ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.RequiresHumanInput(
                new HumanInputRequest(HumanInputKind.Captcha, instructions, null),
                Encoding.UTF8.GetBytes("draft")));

        Assert.NotNull(refusal);
    }

    /// <summary>
    /// A stop that saved nothing to come back to.
    /// </summary>
    /// <remarks>
    /// It reads as a pause and behaves as a restart. Somebody clears the ask, the resumed
    /// attempt has nothing to reload, and the work is done again from the beginning — which
    /// for a demand means a company hearing about the same person twice.
    /// </remarks>
    [Fact]
    public void A_stop_that_cannot_be_resumed_is_refused()
    {
        var refusal = ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.RequiresHumanInput(
                new HumanInputRequest(HumanInputKind.Captcha, "Solve the puzzle shown.", null),
                []));

        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_stop_that_can_be_resumed_and_says_what_it_needs_is_believed()
    {
        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.RequiresHumanInput(
                new HumanInputRequest(
                    HumanInputKind.Captcha,
                    "Solve the puzzle shown.",
                    new Uri("https://example-broker.test/challenge/9f2")),
                Encoding.UTF8.GetBytes("draft"))));
    }

    [Theory]
    [InlineData("file:///tmp/challenge.png")]
    [InlineData("javascript:alert(1)")]
    public void A_challenge_that_is_not_a_web_address_is_refused(string challenge)
    {
        var refusal = ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.RequiresHumanInput(
                new HumanInputRequest(HumanInputKind.Captcha, "Solve the puzzle shown.", new Uri(challenge)),
                Encoding.UTF8.GetBytes("draft")));

        Assert.NotNull(refusal);
    }

    /// <summary>
    /// A receipt that is present and says nothing.
    /// </summary>
    /// <remarks>
    /// Null already means "the company issued no confirmation", so a blank string is a
    /// second spelling of that with the opposite appearance: it reads as evidence a demand
    /// was made until somebody looks at it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_receipt_is_refused(string receipt)
    {
        var refusal = ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.Success(receipt, null));

        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_company_that_issued_no_receipt_is_believed()
    {
        Assert.Null(ConnectorContract.Refuse(
            Needs(IdentityField.Names),
            new ConnectorResult.Success(null, null)));
    }

    [Theory]
    [InlineData("generic-web-form")]
    [InlineData("templated_email")]
    [InlineData("acme.people-search.v2")]
    [InlineData("a")]
    public void A_name_an_attempt_can_be_recorded_under_is_allowed(string connectorId)
    {
        Assert.Null(ConnectorContract.Refuse(
            new ConnectorRegistration(connectorId, new StubConnector())));
    }

    /// <summary>
    /// A registration whose name the attempt could never be stored under.
    /// </summary>
    /// <remarks>
    /// Caught at resolution rather than at the insert, which is the difference between
    /// refusing to run and discovering the problem after a company has already been asked —
    /// leaving an attempt that happened and no row saying it did.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("Generic-Web-Form")]
    [InlineData("-leading-dash")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    public void A_name_that_could_not_be_recorded_is_refused(string connectorId)
    {
        Assert.NotNull(ConnectorContract.Refuse(
            new ConnectorRegistration(connectorId, new StubConnector())));
    }

    [Fact]
    public void A_name_longer_than_the_column_holds_is_refused()
    {
        Assert.NotNull(ConnectorContract.Refuse(
            new ConnectorRegistration(new string('a', 65), new StubConnector())));

        Assert.Null(ConnectorContract.Refuse(
            new ConnectorRegistration(new string('a', 64), new StubConnector())));
    }

    private sealed class StubConnector : IBrokerConnector
    {
        public ConnectorCapabilities Capabilities { get; } = new(
            ConnectorKind.Recipe,
            RemovalMethod.Email,
            new HashSet<IdentityField> { IdentityField.Names });

        public Task<ConnectorResult> ExecuteAsync(
            ConnectorContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult<ConnectorResult>(new ConnectorResult.AlreadyClear());
    }
}
