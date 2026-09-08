// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Messaging;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Removals;
using Dbr.Domain.Search;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.InternalEdge;
using Dbr.Infrastructure.Removals;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Turning a queued demand into work in a company's lane, and an answer into a history.
/// </summary>
/// <remarks>
/// <para>
/// The claims here need a real database and cannot be made anywhere else: that a demand is
/// claimed exactly once however many dispatchers arrive, that a demand nothing can carry is
/// left alone rather than spent, that the role which finds waiting demands sees those and
/// nothing else, and that an answer moves two rows in step.
/// </para>
/// <para>
/// The container is built here rather than taken from the API factory, and that is the
/// point: this is the worker's shape — persistence, lanes, a connector registry — with no
/// vault connection and no key manager in it. A dispatcher or a handler that needed either
/// would fail to resolve, which is a stronger statement than a comment saying it does not.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class RemovalDispatchTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string RemovalsPath = "/api/v1/removal-requests";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private readonly StubBrokerConnectorRegistry _connectors = new();

    private readonly RecordingWorkDispatcher _lanes = new();

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private ServiceProvider _worker = null!;

    private Guid _brokerId;

    private Guid _inactiveBrokerId;

    private string BrokerDomain => $"rmd-broker-{_suffix}.test";

    private string InactiveDomain => $"rmd-gone-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('RMD Broker {_suffix}', '{BrokerDomain}', 'email', 30, true),
                        ('RMD Gone {_suffix}', '{InactiveDomain}', 'email', 30, false);
             """);

        _brokerId = await IdOfAsync(BrokerDomain);
        _inactiveBrokerId = await IdOfAsync(InactiveDomain);

        _worker = BuildWorker();
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync();

        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.removal_job;
             DELETE FROM public.removal_request;
             DELETE FROM vault.exposure_source;
             DELETE FROM public.exposure;
             DELETE FROM public.scan_broker;
             DELETE FROM public.scan;
             DELETE FROM public.consent_record;
             DELETE FROM vault.profile_identity;
             DELETE FROM public.privacy_profile;
             DELETE FROM public.tenant;
             DELETE FROM public.passkey_ceremony;
             DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';
             """);
    }

    /// <summary>
    /// A demand this build has no connector for is left where it is.
    /// </summary>
    /// <remarks>
    /// The state of every demand in this build, and the difference from the scan side. A
    /// scan leg nothing can search is written as finished; a demand is a standing request
    /// that a company be asked, and it has not been asked yet. Failing it would spend an
    /// attempt on this instance's build rather than on the company.
    /// </remarks>
    [Fact]
    public async Task A_demand_with_no_connector_stays_queued()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        var result = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.NoConnector, result.Outcome);
        Assert.Equal("queued", await StatusAsync(requestId));
        Assert.Equal(0, await AttemptsAsync(requestId));
        Assert.Empty(_lanes.Sent);
    }

    [Fact]
    public async Task A_demand_for_a_company_this_instance_no_longer_uses_stays_queued()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _inactiveBrokerId);

        _connectors.With(_inactiveBrokerId, StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()));

        var result = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.BrokerNotDispatchable, result.Outcome);
        Assert.Equal("queued", await StatusAsync(requestId));
    }

    [Fact]
    public async Task A_dispatched_demand_is_submitted_and_carries_an_attempt()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        _connectors.With(
            _brokerId,
            StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()),
            "templated-email");

        var result = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.Dispatched, result.Outcome);
        Assert.Equal("submitted", await StatusAsync(requestId));
        Assert.Equal(1, await AttemptsAsync(requestId));

        var work = Assert.IsType<RemovalJobWork>(Assert.Single(_lanes.Sent));
        Assert.Equal(_brokerId, work.BrokerId);
        Assert.Equal(requestId, work.RemovalRequestId);
        Assert.Equal(1, work.AttemptNumber);

        // The name the attempt is recorded under comes from the registration rather than
        // from the connector, which is what lets one engine serve many companies.
        Assert.Equal("templated-email", await ConnectorOfAsync(work.RemovalJobId));
    }

    /// <summary>
    /// An attempt whose grant will not mint is recorded, and the demand goes back.
    /// </summary>
    /// <remarks>
    /// Reachable because the two checks look at different things: the contract refuses a
    /// connector that declares no fields when it is handed a context, and the dispatcher
    /// mints before any context exists. So this is turned away by the release path, and
    /// what matters is that it does not leave an attempt sitting pending forever — no
    /// message is coming for a grant that was never issued.
    /// </remarks>
    [Fact]
    public async Task An_attempt_whose_grant_will_not_mint_is_recorded_and_requeued()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        _connectors.With(_brokerId, StubBrokerConnector.NeedingNothing());

        var result = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.ReleaseRefused, result.Outcome);
        Assert.Equal("queued", await StatusAsync(requestId));
        Assert.Equal(1, await JobCountAsync(requestId));
        Assert.Empty(_lanes.Sent);

        var jobId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.removal_job WHERE removal_request_id = '{requestId}'");

        Assert.Equal("failed", await JobStatusAsync(jobId));
        Assert.Equal("unsupported", await FailureReasonAsync(jobId));
    }

    /// <summary>
    /// Two dispatchers arriving together send one demand, not two.
    /// </summary>
    /// <remarks>
    /// The claim the whole conditional-update arrangement exists for, and the one that
    /// cannot be made without a real database. The symptom of getting it wrong is a company
    /// receiving the same demand twice in one person's name.
    /// </remarks>
    [Fact]
    public async Task A_demand_is_claimed_exactly_once()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        _connectors.With(_brokerId, StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()));

        var first = DispatchAsync(account, requestId);
        var second = DispatchAsync(account, requestId);

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == RemovalDispatchOutcome.Dispatched);
        Assert.Single(results, result => result.Outcome == RemovalDispatchOutcome.NotClaimable);
        Assert.Single(_lanes.Sent);
        Assert.Equal(1, await JobCountAsync(requestId));
    }

    [Fact]
    public async Task A_demand_that_is_not_queued_is_not_dispatched()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        _connectors.With(_brokerId, StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()));

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET status = 'cancelled' WHERE id = '{requestId}'");

        var result = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.NotClaimable, result.Outcome);
        Assert.Empty(_lanes.Sent);
    }

    /// <summary>
    /// The role that finds waiting demands sees those and nothing else.
    /// </summary>
    /// <remarks>
    /// Asserted against the real policy rather than the query's own WHERE. A dispatched
    /// demand being invisible is what stops this privilege from being a way to watch what
    /// an account is doing.
    /// </remarks>
    [Fact]
    public async Task The_directory_sees_waiting_demands_and_not_dispatched_ones()
    {
        var account = await OpenAccountAsync();
        var waiting = await OpenDemandAsync(account, _brokerId);

        var directory = _worker.GetRequiredService<IQueuedRemovalDirectory>();

        var before = await directory.ListQueuedAsync(50, TestContext.Current.CancellationToken);
        Assert.Contains(before, row => row.RemovalRequestId == waiting);

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET status = 'submitted' WHERE id = '{waiting}'");

        var after = await directory.ListQueuedAsync(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(after, row => row.RemovalRequestId == waiting);
    }

    /// <summary>
    /// A company that accepted the demand leaves it waiting, with the attempt recorded.
    /// </summary>
    [Fact]
    public async Task An_accepted_demand_starts_the_clock()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.Success("TICKET-9", null)));

        await HandleAsync(account, work);

        Assert.Equal("awaiting_broker_response", await StatusAsync(requestId));
        Assert.Equal("succeeded", await JobStatusAsync(work.RemovalJobId));
        Assert.Null(await FailureReasonAsync(work.RemovalJobId));
        Assert.Contains("TICKET-9", await DetailAsync(work.RemovalJobId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_left_to_remove_ends_the_demand()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()));

        await HandleAsync(account, work);

        Assert.Equal("removed", await StatusAsync(requestId));
        Assert.Equal("succeeded", await JobStatusAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A failure worth repeating goes back to the queue rather than resting.
    /// </summary>
    /// <remarks>
    /// Straight back, so the dispatcher picks it up on its next pass and there is one path
    /// into a company's lane rather than two. The next-attempt time lands on the attempt
    /// that failed, because backoff is a fact about what just happened.
    /// </remarks>
    [Fact]
    public async Task A_failure_worth_repeating_returns_to_the_queue()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.Failed(
                ConnectorFailureReason.Transient,
                "the connection was reset",
                Retryable: true)));

        await HandleAsync(account, work);

        Assert.Equal("queued", await StatusAsync(requestId));
        Assert.Equal("failed", await JobStatusAsync(work.RemovalJobId));
        Assert.Equal("transient", await FailureReasonAsync(work.RemovalJobId));
        Assert.NotNull(await NextRetryAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A company that refused is not asked again.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_not_returned_to_the_queue()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.Failed(
                ConnectorFailureReason.Rejected,
                "we do not hold data for this person",
                Retryable: false)));

        await HandleAsync(account, work);

        Assert.Equal("failed", await StatusAsync(requestId));
        Assert.Equal("rejected", await FailureReasonAsync(work.RemovalJobId));
        Assert.Null(await NextRetryAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A refusal that asks to be retried never reaches the mapping at all.
    /// </summary>
    /// <remarks>
    /// The contract refuses that combination outright, so the attempt ends as a connector
    /// that answered in a way it was not entitled to rather than as a company that said no.
    /// Worth pinning here because it is the composition that matters: the rule is stated
    /// once in the contract and once in the mapping, and this is which of the two a real
    /// answer meets first. Losing the contract check would silently promote a broken
    /// connector's answer into a legitimate-looking refusal.
    /// </remarks>
    [Fact]
    public async Task A_refusal_claiming_to_be_retryable_is_refused_before_it_is_read()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.Failed(
                ConnectorFailureReason.Rejected,
                "we do not hold data for this person",
                Retryable: true)));

        await HandleAsync(account, work);

        Assert.Equal("failed", await StatusAsync(requestId));
        Assert.Equal("unsupported", await FailureReasonAsync(work.RemovalJobId));
        Assert.Null(await NextRetryAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A demand that has used its budget stops rather than looping.
    /// </summary>
    [Fact]
    public async Task A_demand_that_has_used_its_attempts_does_not_return_to_the_queue()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.Failed(
                ConnectorFailureReason.Transient,
                "the connection was reset",
                Retryable: true)));

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET attempt = 3 WHERE id = '{requestId}'");

        await HandleAsync(account, work);

        Assert.Equal("failed", await StatusAsync(requestId));
        Assert.Null(await NextRetryAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A connector that throws decided nothing, and is not tried again.
    /// </summary>
    /// <remarks>
    /// Recorded as its own answer rather than allowed to escape. Throwing would hand the
    /// message back to the transport, and a redelivered demand is a company asked twice in
    /// one person's name.
    /// </remarks>
    [Fact]
    public async Task A_connector_that_throws_ends_the_attempt_rather_than_the_message()
    {
        var (account, requestId, work) = await DispatchedAsync(StubBrokerConnector.Throwing());

        await HandleAsync(account, work);

        Assert.Equal("failed", await StatusAsync(requestId));
        Assert.Equal("unsupported", await FailureReasonAsync(work.RemovalJobId));
        Assert.Null(await NextRetryAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A repeat delivery does not send the demand a second time.
    /// </summary>
    /// <remarks>
    /// The transport is entitled to deliver twice. The attempt is the guard: it is pending
    /// exactly once, and anything else means the work has already been done.
    /// </remarks>
    [Fact]
    public async Task A_repeat_delivery_is_ignored()
    {
        var ran = 0;

        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(_ =>
            {
                ran++;

                return new ConnectorResult.AlreadyClear();
            }));

        await HandleAsync(account, work);
        await HandleAsync(account, work);

        Assert.Equal(1, ran);
        Assert.Equal("removed", await StatusAsync(requestId));
    }

    /// <summary>
    /// A connector resolved for the wrong kind of company is refused before it runs.
    /// </summary>
    /// <remarks>
    /// It would otherwise fail looking like a company that changed its site, and spend that
    /// entry's retries proving it.
    /// </remarks>
    [Fact]
    public async Task A_connector_that_acts_the_wrong_way_is_refused_before_it_runs()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.ForMethod(RemovalMethod.WebForm));

        await HandleAsync(account, work);

        Assert.Equal("failed", await StatusAsync(requestId));
        Assert.Equal("unsupported", await FailureReasonAsync(work.RemovalJobId));
    }

    /// <summary>
    /// The connector is handed a demand describing what is being asked and by when.
    /// </summary>
    [Fact]
    public async Task A_connector_is_told_what_is_demanded()
    {
        var connector = StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear());
        var (account, _, work) = await DispatchedAsync(connector);

        await HandleAsync(account, work);

        var context = Assert.IsType<ConnectorContext>(connector.LastContext);

        Assert.Equal(LegalRequestType.Delete, context.Demand.RequestType);
        Assert.Equal(DeadlineSource.OperationalDefault, context.Demand.Source);
        Assert.Null(context.Demand.StatuteCode);
        Assert.Equal(BrokerDomain, context.Broker.Domain);
        Assert.Equal(1, context.AttemptNumber);

        // The listing is in the vault under a key of its own that nothing mints a release
        // for yet, so a demand cites nothing — which is an ordinary demand.
        Assert.Null(context.SourceRef);
    }

    /// <summary>
    /// The connector receives exactly the groups it declared, and nothing else.
    /// </summary>
    /// <remarks>
    /// The guarantee the whole scoped release exists for, and the one worth asserting from
    /// both ends: this connector names only <c>Names</c>, and the profile behind it has a
    /// date of birth and a contact on file. Both stay in the vault. The point is not that
    /// the connector chose not to look — it is that there was no moment at which it could,
    /// because the grant minted for this attempt never covered them.
    /// </remarks>
    [Fact]
    public async Task A_connector_is_given_the_groups_it_declared_and_no_others()
    {
        var connector = StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear());
        var (account, _, work) = await DispatchedAsync(connector);

        await HandleAsync(account, work);

        var identity = Assert.IsType<ConnectorContext>(connector.LastContext).ReleasedIdentity;

        Assert.Equal(["Alex Whitfield"], identity.Names);

        // On the profile, and never asked for.
        Assert.Null(identity.DateOfBirth);
        Assert.Empty(identity.Contacts);
        Assert.Empty(identity.Addresses);
    }

    /// <summary>
    /// A grant is single-use, so a second attempt on the same message opens nothing.
    /// </summary>
    /// <remarks>
    /// Not reachable through the ordinary path — the attempt guard stops a repeat delivery
    /// before the grant is presented — so this spends the token by hand first and then
    /// hands the message over. What is being asserted is that the release, and not only the
    /// attempt row, is what stops the second run.
    /// </remarks>
    [Fact]
    public async Task A_grant_that_has_already_been_spent_opens_nothing()
    {
        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()));

        Assert.NotNull(await RedeemAsync(work.ReleaseToken, TestContext.Current.CancellationToken));

        await HandleAsync(account, work);

        Assert.Equal("queued", await StatusAsync(requestId));
        Assert.Equal("failed", await JobStatusAsync(work.RemovalJobId));
        Assert.Equal("transient", await FailureReasonAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A grant minted for an attempt cannot be spent to record findings.
    /// </summary>
    /// <remarks>
    /// The two spends are different permissions over one token, and only a scan leg has the
    /// second. Findings belong to the run that found them; filing one against a demand would
    /// mean an exposure hanging off a search that never happened.
    /// </remarks>
    [Fact]
    public async Task An_attempts_grant_cannot_be_spent_on_findings()
    {
        var (_, _, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.AlreadyClear()));

        using var scope = _factory.Services.CreateScope();

        var reported = await scope.ServiceProvider
            .GetRequiredService<IFindingReporter>()
            .ReportAsync(
                work.ReleaseToken,
                [
                    new ReportedListing(
                        new Uri($"https://{BrokerDomain}/profile/1"),
                        [new FieldMatch(IdentityField.Names, MatchStrength.Exact)]),
                ],
                TestContext.Current.CancellationToken);

        Assert.NotEqual(ReportFindingsOutcome.Recorded, reported.Outcome);
        Assert.Equal(0, await ExposureCountAsync());
    }

    private async Task<long> ExposureCountAsync() =>
        await postgres.QueryAsOwnerAsync<long>("SELECT count(*) FROM public.exposure");

    /// <summary>
    /// A demand cancelled while its attempt was in the lane is not sent.
    /// </summary>
    /// <remarks>
    /// The window is real: the lane paces work, so an attempt can sit in it for as long as
    /// the company's row says, and a person can press cancel in that time. Running anyway
    /// would send a company a demand somebody had withdrawn — the one outcome the cancel
    /// route exists to prevent — and the answer would then have nowhere legal to go, which
    /// turns one cancelled demand into a message the transport redelivers forever.
    /// </remarks>
    [Fact]
    public async Task A_demand_cancelled_while_its_attempt_waited_is_not_sent()
    {
        var ran = 0;

        var (account, requestId, work) = await DispatchedAsync(
            StubBrokerConnector.Answering(_ =>
            {
                ran++;

                return new ConnectorResult.Success("TICKET-1", null);
            }));

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET status = 'cancelled' WHERE id = '{requestId}'");

        await HandleAsync(account, work);

        Assert.Equal(0, ran);
        Assert.Equal("cancelled", await StatusAsync(requestId));
        Assert.Equal("failed", await JobStatusAsync(work.RemovalJobId));
    }

    /// <summary>
    /// A demand that failed and went back to the queue is sent again as a second attempt.
    /// </summary>
    /// <remarks>
    /// The retry loop end to end, and the only place the attempt count means anything: a
    /// first attempt is always one, so nothing that dispatches once can tell a counter that
    /// increments from one that is hard-coded.
    /// </remarks>
    [Fact]
    public async Task A_second_attempt_is_numbered_and_recorded_as_its_own()
    {
        var (account, requestId, first) = await DispatchedAsync(
            StubBrokerConnector.Answering(new ConnectorResult.Failed(
                ConnectorFailureReason.Transient,
                "the connection was reset",
                Retryable: true)));

        await HandleAsync(account, first);

        Assert.Equal("queued", await StatusAsync(requestId));

        var second = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.Dispatched, second.Outcome);
        Assert.Equal(2, second.Work!.AttemptNumber);
        Assert.Equal(2, await AttemptsAsync(requestId));

        // Two rows rather than one updated in place, which is what makes a retry failing
        // the same way distinguishable from one failing differently.
        Assert.Equal(2, await JobCountAsync(requestId));
        Assert.NotEqual(first.RemovalJobId, second.Work.RemovalJobId);
    }

    /// <summary>
    /// The row-level policy is what hides a dispatched demand, not the query's own filter.
    /// </summary>
    /// <remarks>
    /// Asserted directly as the role, because going through the directory cannot tell the
    /// two apart: deleting the <c>WHERE</c> from that query changes nothing while the policy
    /// stands, which is the arrangement working rather than a test that is not looking.
    /// </remarks>
    [Fact]
    public async Task The_scheduler_role_cannot_see_a_demand_once_it_is_dispatched()
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        Assert.Equal(1, await VisibleToSchedulerAsync(requestId));

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET status = 'submitted' WHERE id = '{requestId}'");

        Assert.Equal(0, await VisibleToSchedulerAsync(requestId));
    }

    /// <summary>How many rows this id is, seen as the role that finds waiting demands.</summary>
    private async Task<int> VisibleToSchedulerAsync(Guid requestId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var role = new NpgsqlCommand("SET ROLE dbr_scheduler;", connection);
        await role.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // No status filter, deliberately: whatever comes back is what the policy allows.
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM public.removal_request WHERE id = '{requestId}';",
            connection);

        return (int)(long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<(Account Account, Guid RequestId, RemovalJobWork Work)> DispatchedAsync(
        IBrokerConnector connector)
    {
        var account = await OpenAccountAsync();
        var requestId = await OpenDemandAsync(account, _brokerId);

        _connectors.With(_brokerId, connector);

        var result = await DispatchAsync(account, requestId);

        Assert.Equal(RemovalDispatchOutcome.Dispatched, result.Outcome);

        return (account, requestId, result.Work!);
    }

    private async Task<RemovalDispatchResult> DispatchAsync(Account account, Guid requestId)
    {
        await using var scope = _worker.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(account.TenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IRemovalDispatcher>()
            .DispatchAsync(requestId, TestContext.Current.CancellationToken);
    }

    private async Task HandleAsync(Account account, RemovalJobWork work)
    {
        await using var scope = _worker.CreateAsyncScope();

        // The handler establishes the tenant from the message, which is what a consumer
        // does. Setting it here as well would hide a handler that had stopped doing so.
        _ = account;

        await scope.ServiceProvider
            .GetRequiredService<RemovalJobWorkHandler>()
            .HandleAsync(work, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> OpenDemandAsync(Account account, Guid brokerId)
    {
        var (status, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId, requestType = "delete" },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, status);

        return body.GetProperty("id").GetGuid();
    }

    private async Task<Guid> IdOfAsync(string domain) =>
        await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{domain}'");

    private async Task<string?> StatusAsync(Guid requestId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.removal_request WHERE id = '{requestId}'");

    private async Task<int> AttemptsAsync(Guid requestId) =>
        await postgres.QueryAsOwnerAsync<int>(
            $"SELECT attempt FROM public.removal_request WHERE id = '{requestId}'");

    private async Task<long> JobCountAsync(Guid requestId) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.removal_job WHERE removal_request_id = '{requestId}'");

    private async Task<string?> JobStatusAsync(Guid jobId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.removal_job WHERE id = '{jobId}'");

    private async Task<string?> ConnectorOfAsync(Guid jobId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT connector_id FROM public.removal_job WHERE id = '{jobId}'");

    private async Task<string?> FailureReasonAsync(Guid jobId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT failure_reason FROM public.removal_job WHERE id = '{jobId}'");

    private async Task<string?> DetailAsync(Guid jobId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT detail FROM public.removal_job WHERE id = '{jobId}'");

    private async Task<DateTime?> NextRetryAsync(Guid jobId) =>
        await postgres.QueryAsOwnerAsync<DateTime?>(
            $"SELECT next_retry_at FROM public.removal_job WHERE id = '{jobId}'");

    private async Task<Account> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"rmd-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        foreach (var scope in new[] { "scan", "auto_removal" })
        {
            await _api.PostAsync(
                ConsentPath,
                new { scope, granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
                token);
        }

        // A real identity on the profile, because the whole point of a grant is that a
        // connector receives some of it — and an empty profile makes a scoped release and a
        // broken one look identical.
        await _api.PutAsync(
            "/api/v1/profile",
            new
            {
                names = new[] { "Alex Whitfield" },
                dateOfBirth = "1985-04-17",
                contacts = new[] { new { kind = "email", value = "alex@example.test" } },
            },
            token);

        var (_, scan) = await _api.PostAsync(ScansPath, new { }, token);

        return new Account(token, ApiClient.TenantId(session), scan.GetProperty("profileId").GetGuid());
    }

    /// <summary>
    /// The worker's container: no vault, no key manager, nothing that can decrypt.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than taken from the API factory, so that what the dispatcher and
    /// the handler are able to resolve is the assertion rather than a comment.
    /// </remarks>
    private ServiceProvider BuildWorker()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Core"] = postgres.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbrPersistence(configuration);

        // Minting, and nothing that can open what it mints. The claim this container makes
        // is the same one the scan dispatcher's does: a grant is a row of random bytes
        // against the core store, so the process that sends demands can write one without
        // ever acquiring the ability to spend it.
        services.AddDbrReleaseMinting(configuration);

        // Spending it goes to the process that does hold the keys, which here is the API
        // factory's container rather than a listener with mutual TLS in front of it. What is
        // being asserted is what an attempt does with a real identity, not the handshake.
        services.AddSingleton<IReleaseClient>(new DirectReleaseClient(RedeemAsync));

        services.AddSingleton<IBrokerConnectorRegistry>(_connectors);
        services.AddSingleton<IBrokerWorkDispatcher>(_lanes);
        services.AddSingleton<IQueuedRemovalDirectory>(
            new QueuedRemovalDirectory(postgres.ConnectionString));
        services.AddSingleton(Options.Create(new RemovalOptions()));
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IRemovalDispatcher, RemovalDispatcher>();
        services.AddScoped<RemovalJobWorkHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The edge, without the edge: a real redemption, shaped the way the route shapes it.
    /// </summary>
    /// <remarks>
    /// The grant is spent against the real service, so the identity a connector receives is
    /// genuinely the one the vault released for that token — including which groups it
    /// covered and which it left alone.
    /// </remarks>
    private async Task<ReleaseResponse?> RedeemAsync(string token, CancellationToken cancellationToken)
    {
        using var scope = _factory.Services.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseRedeemer>()
            .RedeemAsync(token, cancellationToken);

        if (result.Release is not { } release)
        {
            return null;
        }

        return new ReleaseResponse(
            release.ScanId,
            release.RemovalJobId,
            release.BrokerId,
            [.. release.Fields.Select(IdentityVocabulary.ToWire)],
            release.Identity.Names,
            [
                .. release.Identity.Addresses.Select(address => new ReleasedAddress(
                    address.Id,
                    address.Line1,
                    address.Line2,
                    address.City,
                    address.Region,
                    address.PostalCode,
                    address.Country)),
            ],
            [
                .. release.Identity.Contacts.Select(contact => new ReleasedContact(
                    contact.Id,
                    contact.Kind.ToString().ToLowerInvariant(),
                    contact.Value)),
            ],
            release.Identity.DateOfBirth);
    }

    private sealed record Account(string Token, Guid TenantId, Guid ProfileId);
}
