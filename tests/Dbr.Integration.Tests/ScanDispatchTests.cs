// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Messaging;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Turning a queued run into work sitting in each company's lane.
/// </summary>
/// <remarks>
/// <para>
/// The claims here are about a real database and cannot be made anywhere else: that a run
/// is claimed exactly once however many dispatchers arrive, that the grant minted for a leg
/// covers exactly the groups its search declared, and that the role which finds waiting runs
/// can see those and nothing else.
/// </para>
/// <para>
/// The container is built here rather than taken from the API factory, and that is the
/// point of the story: this is the worker's shape — persistence, minting, lanes, a search
/// registry — with no vault connection and no key manager in it. A dispatcher that needed
/// either would fail to resolve, which is a stronger statement than a comment saying it
/// does not.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ScanDispatchTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private const string ProfilePath = "/api/v1/profile";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private readonly StubBrokerSearchRegistry _searches = new();

    private readonly RecordingWorkDispatcher _lanes = new();

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private ServiceProvider _worker = null!;

    private Guid _brokerId;

    private Guid _otherBrokerId;

    private Guid _inactiveBrokerId;

    private string BrokerDomain => $"dispatch-broker-{_suffix}.test";

    private string OtherBrokerDomain => $"dispatch-other-{_suffix}.test";

    private string InactiveBrokerDomain => $"dispatch-inactive-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Dispatch Broker {_suffix}', '{BrokerDomain}', 'webform', 45, true),
                        ('Dispatch Other {_suffix}', '{OtherBrokerDomain}', 'email', 30, true),
                        ('Dispatch Gone {_suffix}', '{InactiveBrokerDomain}', 'email', 30, false);
             """);

        _brokerId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");
        _otherBrokerId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{OtherBrokerDomain}'");
        _inactiveBrokerId = await IdOfAsync(
            $"SELECT id FROM public.broker WHERE domain = '{InactiveBrokerDomain}'");

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
             DELETE FROM vault.exposure_source;
             DELETE FROM public.exposure;
             DELETE FROM public.scan_leg;
             DELETE FROM public.identity_release;
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
    /// The hole this story fills: a scan used to be queued and stay queued forever.
    /// </summary>
    [Fact]
    public async Task A_queued_run_is_claimed_and_its_companies_get_work()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches
            .With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names))
            .With(_otherBrokerId, StubBrokerSearch.Needing(IdentityField.Names));

        var result = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(ScanDispatchOutcome.Started, result.Outcome);
        Assert.Equal(2, result.Planned);
        Assert.Equal(0, result.Unplannable);

        Assert.Equal("running", await StatusOfAsync(scanId));

        var addressed = _lanes.Sent.Cast<ScanBrokerWork>().Select(work => work.BrokerId).ToHashSet();

        Assert.Equal([_brokerId, _otherBrokerId], addressed);
    }

    /// <summary>
    /// The claim is what stops one company being asked twice about one person.
    /// </summary>
    /// <remarks>
    /// Sequential rather than racing, because the claim is a single conditional statement
    /// and the second caller is reading the row the first one already changed. What a race
    /// would add here is coverage of the database's own serialisation, which is not this
    /// code's claim to make.
    /// </remarks>
    [Fact]
    public async Task A_run_somebody_has_already_started_cannot_be_started_again()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches.With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names));

        Assert.Equal(ScanDispatchOutcome.Started, (await DispatchAsync(account.TenantId, scanId)).Outcome);

        var second = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(ScanDispatchOutcome.NotClaimable, second.Outcome);

        // And no second helping of work for the company, which is what the claim is for.
        Assert.Single(_lanes.Sent);
    }

    [Fact]
    public async Task A_run_narrowed_to_one_company_asks_only_that_one()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        _searches
            .With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names))
            .With(_otherBrokerId, StubBrokerSearch.Needing(IdentityField.Names));

        var result = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(1, result.Planned);
        Assert.Equal([_brokerId], await LegBrokersOfAsync(scanId));
    }

    /// <summary>
    /// An entry an operator deactivated is one this instance decided not to dispatch
    /// against.
    /// </summary>
    [Fact]
    public async Task A_company_that_is_no_longer_active_gets_no_leg()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches
            .With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names))
            .With(_inactiveBrokerId, StubBrokerSearch.Needing(IdentityField.Names));

        await DispatchAsync(account.TenantId, scanId);

        Assert.DoesNotContain(_inactiveBrokerId, await LegBrokersOfAsync(scanId));
    }

    /// <summary>
    /// Most of the catalog has no search, and a run should say so rather than stall.
    /// </summary>
    [Fact]
    public async Task A_company_nothing_knows_how_to_search_is_recorded_rather_than_skipped()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches.With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names));

        var result = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(1, result.Planned);
        Assert.Equal(1, result.Unplannable);

        var outcome = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT outcome FROM public.scan_leg "
            + $"WHERE scan_id = '{scanId}' AND broker_id = '{_otherBrokerId}'");

        Assert.Equal("no_search_available", outcome);

        // Recorded and over: nothing is coming for that company, so its leg is finished.
        var unfinished = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.scan_leg "
            + $"WHERE scan_id = '{scanId}' AND broker_id = '{_otherBrokerId}' "
            + $"AND completed_at IS NULL");

        Assert.Equal(0, unfinished);
    }

    /// <summary>
    /// A run nothing can search is a run that is over, not one that waits forever.
    /// </summary>
    [Fact]
    public async Task A_run_where_no_leg_could_be_sent_finishes_immediately()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var result = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(0, result.Planned);
        Assert.Equal(2, result.Unplannable);

        // Failed rather than completed: the companies in scope were not reached, which is
        // what the status says it means. Which ones, and why, is on the leg rows.
        Assert.Equal("failed", await StatusOfAsync(scanId));
    }

    /// <summary>
    /// Nobody to ask is an answer, and every one of nobody was reached.
    /// </summary>
    [Fact]
    public async Task A_run_with_nobody_in_scope_completes()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        // Every company out of the way, so the run is claimed and finds nothing to plan.
        await postgres.ExecuteAsOwnerAsync("UPDATE public.broker SET active = false");

        var result = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(ScanDispatchOutcome.NothingInScope, result.Outcome);
        Assert.Equal("completed", await StatusOfAsync(scanId));
    }

    /// <summary>
    /// The grant covers what the search declared, and the declaration is read before it is
    /// minted.
    /// </summary>
    /// <remarks>
    /// The property the whole release design rests on, seen from the dispatch side: a
    /// search that never names a date of birth cannot cause one to be decrypted, because
    /// there is no moment at which it could — the grant it travels with does not cover one.
    /// </remarks>
    [Fact]
    public async Task A_leg_carries_a_grant_covering_exactly_what_its_search_needs()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        _searches.With(
            _brokerId,
            StubBrokerSearch.Needing(IdentityField.Names, IdentityField.Addresses));

        await DispatchAsync(account.TenantId, scanId);

        var fields = await postgres.QueryAsOwnerAsync<string[]>(
            $"SELECT fields FROM public.identity_release WHERE scan_id = '{scanId}'");

        // Asserted present before it is read: no row at all and a row covering no fields are
        // different failures, and Order() on the first would report the second.
        Assert.NotNull(fields);
        Assert.Equal(["addresses", "names"], fields.Order().ToArray());
    }

    [Fact]
    public async Task One_grant_is_minted_for_each_company_rather_than_one_for_the_run()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches
            .With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names))
            .With(_otherBrokerId, StubBrokerSearch.Needing(IdentityField.Names));

        await DispatchAsync(account.TenantId, scanId);

        var brokers = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(DISTINCT broker_id) FROM public.identity_release WHERE scan_id = '{scanId}'");

        Assert.Equal(2, brokers);

        // And the tokens differ, which is what keeps a leaked one to a single company's leg.
        var digests = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(DISTINCT token_hash) FROM public.identity_release WHERE scan_id = '{scanId}'");

        Assert.Equal(2, digests);
    }

    /// <summary>
    /// A leg exists before its work is sent, or there would be nothing to record against.
    /// </summary>
    [Fact]
    public async Task Every_piece_of_work_sent_has_a_leg_waiting_for_it()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches
            .With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names))
            .With(_otherBrokerId, StubBrokerSearch.Needing(IdentityField.Names));

        await DispatchAsync(account.TenantId, scanId);

        var legs = await LegBrokersOfAsync(scanId);

        foreach (var work in _lanes.Sent.Cast<ScanBrokerWork>())
        {
            Assert.Contains(work.BrokerId, legs);
        }
    }

    [Fact]
    public async Task Work_carries_the_run_the_account_and_the_identity_being_searched_for()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        _searches.With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names));

        await DispatchAsync(account.TenantId, scanId);

        var work = Assert.IsType<ScanBrokerWork>(Assert.Single(_lanes.Sent));

        Assert.Equal(scanId, work.ScanId);
        Assert.Equal(account.TenantId, work.TenantId);
        Assert.Equal(1, work.AttemptNumber);
        Assert.NotEqual(string.Empty, work.ReleaseToken);

        var onScan = await IdOfAsync($"SELECT privacy_profile_id FROM public.scan WHERE id = '{scanId}'");

        Assert.Equal(onScan, work.PrivacyProfileId);
    }

    /// <summary>
    /// Somebody else's run is not a run, which is the answer everywhere else too.
    /// </summary>
    [Fact]
    public async Task A_dispatcher_acting_for_the_wrong_account_claims_nothing()
    {
        var owner = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(owner.Token);

        var stranger = await OpenScanningAccountAsync();

        var result = await DispatchAsync(stranger.TenantId, scanId);

        Assert.Equal(ScanDispatchOutcome.NotClaimable, result.Outcome);
        Assert.Equal("queued", await StatusOfAsync(scanId));
    }

    // ---------------------------------------------------------------------------------
    // The role that finds waiting runs
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The one question no tenant-scoped role can answer: what is waiting, and whose.
    /// </summary>
    [Fact]
    public async Task Waiting_runs_are_visible_across_accounts()
    {
        var first = await OpenScanningAccountAsync();
        var firstScan = await QueueScanAsync(first.Token);

        var second = await OpenScanningAccountAsync();
        var secondScan = await QueueScanAsync(second.Token);

        var waiting = await _worker
            .GetRequiredService<IQueuedScanDirectory>()
            .ListQueuedAsync(50, TestContext.Current.CancellationToken);

        var found = waiting.Select(scan => scan.ScanId).ToHashSet();

        Assert.Contains(firstScan, found);
        Assert.Contains(secondScan, found);

        Assert.Equal(
            first.TenantId,
            waiting.Single(scan => scan.ScanId == firstScan).TenantId);
    }

    /// <summary>
    /// It finds work nobody has picked up, and cannot be used to watch an account.
    /// </summary>
    /// <remarks>
    /// The narrower half of the relaxation, and the half worth testing: the policy names
    /// the queued rows, so a run that has been claimed is invisible to this role entirely.
    /// A grant alone would have handed it every run on the instance for as long as they
    /// existed.
    /// </remarks>
    [Fact]
    public async Task A_run_that_has_been_started_is_invisible_to_the_role_that_finds_them()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        _searches.With(_brokerId, StubBrokerSearch.Needing(IdentityField.Names));

        await DispatchAsync(account.TenantId, scanId);

        var waiting = await _worker
            .GetRequiredService<IQueuedScanDirectory>()
            .ListQueuedAsync(50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(scanId, waiting.Select(scan => scan.ScanId));
    }

    /// <summary>
    /// Whose identity a run is for is none of that role's business.
    /// </summary>
    [Fact]
    public async Task The_role_that_finds_runs_cannot_read_which_identity_one_is_for()
    {
        var account = await OpenScanningAccountAsync();
        await QueueScanAsync(account.Token);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                """
                SET ROLE dbr_scheduler;
                SELECT privacy_profile_id FROM public.scan LIMIT 1;
                """));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_role_that_finds_runs_cannot_start_one_itself()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_scheduler;
                 UPDATE public.scan SET status = 'running' WHERE id = '{scanId}';
                 """));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The worker's container: persistence, minting, lanes, searches.
    /// </summary>
    /// <remarks>
    /// <b>What is absent is the assertion.</b> No <c>AddDbrVault</c> and no
    /// <c>AddDbrKeyManagement</c>, so nothing resolvable from here can decrypt — and the
    /// dispatcher resolves out of it, which means planning a scan genuinely does not need
    /// the ability to open one. Building the API's container instead would have proved the
    /// opposite of what this story is about.
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
        services.AddDbrReleaseMinting(configuration);

        services.AddSingleton<IBrokerSearchRegistry>(_searches);
        services.AddSingleton<IBrokerWorkDispatcher>(_lanes);
        services.AddSingleton<IQueuedScanDirectory>(
            new QueuedScanDirectory(postgres.ConnectionString));

        services.AddScoped<ScanCompletion>();
        services.AddScoped<IScanDispatcher, ScanDispatcher>();

        return services.BuildServiceProvider();
    }

    private async Task<ScanDispatchResult> DispatchAsync(Guid tenantId, Guid scanId)
    {
        using var scope = _worker.CreateScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IScanDispatcher>()
            .DispatchAsync(scanId, TestContext.Current.CancellationToken);
    }

    private async Task<(string Token, Guid TenantId)> OpenScanningAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"dispatch-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);
        var tenantId = ApiClient.TenantId(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        await _api.PutAsync(
            ProfilePath,
            new
            {
                names = new[] { "Alex Whitfield" },
                dateOfBirth = "1985-04-17",
                contacts = new[] { new { kind = "email", value = "alex@example.test" } },
            },
            token);

        await _api.PostAsync(
            $"{ProfilePath}/addresses",
            new
            {
                line1 = "12 Rowan Lane",
                city = "Sacramento",
                region = "CA",
                postalCode = "95814",
                country = "US",
            },
            token);

        return (token, tenantId);
    }

    private async Task<Guid> QueueScanAsync(string token, params Guid[] brokerIds)
    {
        var body = brokerIds.Length > 0
            ? new { brokerIds } as object
            : new { };

        var (status, scan) = await _api.PostAsync(ScansPath, body, token);

        Assert.Equal(HttpStatusCode.Accepted, status);

        return scan.GetProperty("id").GetGuid();
    }

    private async Task<HashSet<Guid>> LegBrokersOfAsync(Guid scanId)
    {
        var ids = await postgres.QueryManyAsOwnerAsync<Guid>(
            $"SELECT broker_id FROM public.scan_leg WHERE scan_id = '{scanId}'");

        return [.. ids];
    }

    /// <summary>
    /// The run's status, which every caller here expects to exist.
    /// </summary>
    /// <remarks>
    /// Null would mean the scan is not in the table at all, which no test here is about —
    /// so it fails as a missing row rather than as an unequal string three lines later.
    /// </remarks>
    private async Task<string> StatusOfAsync(Guid scanId)
    {
        var status = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.scan WHERE id = '{scanId}'");

        Assert.NotNull(status);

        return status;
    }

    private Task<Guid> IdOfAsync(string sql) => postgres.QueryAsOwnerAsync<Guid>(sql);
}
