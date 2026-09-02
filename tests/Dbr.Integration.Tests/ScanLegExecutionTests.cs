// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Messaging;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.InternalEdge;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// A leg running: spending its grant, asking the company, and writing down what came back.
/// </summary>
/// <remarks>
/// <para>
/// The whole loop, end to end, with one stand-in. A run is asked for over HTTP, the
/// dispatcher claims it and mints a grant per company, and each piece of work is then
/// handed to the handler exactly as a lane would hand it over. The grant is spent against
/// the real release service, so the identity a search receives is genuinely the one the
/// vault decrypted for that token — the only invented thing is what the broker's website
/// says, which is not something a test can have an opinion about.
/// </para>
/// <para>
/// <b>The two containers are the point.</b> The worker's has no vault and no key manager;
/// the API's has both. The grant crosses between them, which is the arrangement the whole
/// release design exists to make possible, and building one container for both halves
/// would have quietly tested something else.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ScanLegExecutionTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private const string ProfilePath = "/api/v1/profile";

    private static readonly Uri Listing = new("https://listings.example.test/profile/1");

    private static readonly Uri OtherListing = new("https://listings.example.test/profile/2");

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

    private string BrokerDomain => $"leg-broker-{_suffix}.test";

    private string OtherBrokerDomain => $"leg-other-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Leg Broker {_suffix}', '{BrokerDomain}', 'webform', 45, true),
                        ('Leg Other {_suffix}', '{OtherBrokerDomain}', 'email', 30, true);
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");

        _otherBrokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{OtherBrokerDomain}'");

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
    /// A run that finds something, from asking for it to the finding somebody is shown.
    /// </summary>
    [Fact]
    public async Task A_listing_that_agrees_on_enough_becomes_a_finding()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        // A name and an address, both agreeing exactly: the ordinary people-search result,
        // and comfortably over the bar.
        Search(_brokerId, Found(
            new FieldMatch(IdentityField.Names, MatchStrength.Exact),
            new FieldMatch(IdentityField.Addresses, MatchStrength.Exact)));

        await RunAsync(account.TenantId, scanId);

        Assert.Equal("found", await OutcomeOfAsync(scanId, _brokerId));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM public.exposure WHERE scan_id = '{scanId}'"));

        var confidence = await postgres.QueryAsOwnerAsync<double>(
            $"SELECT confidence FROM public.exposure WHERE scan_id = '{scanId}'");

        Assert.Equal(0.5, confidence, 1e-9);

        var status = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.exposure WHERE scan_id = '{scanId}'");

        Assert.Equal("new", status);

        // A run that reached its one company and recorded its answer.
        Assert.Equal("completed", await ScanStatusOfAsync(scanId));
    }

    /// <summary>
    /// The decision from the confidence story, seen where it actually has an effect.
    /// </summary>
    /// <remarks>
    /// A name and nothing else is the finding the bar exists to hold back. Nothing is
    /// written — not written and hidden, not written with a flag — and the only thing left
    /// behind is the count, which is what tells somebody the bar is in the wrong place.
    /// </remarks>
    [Fact]
    public async Task A_listing_that_only_shares_a_name_is_counted_and_not_kept()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        Search(_brokerId, Found(new FieldMatch(IdentityField.Names, MatchStrength.Exact)));

        await RunAsync(account.TenantId, scanId);

        Assert.Equal("found", await OutcomeOfAsync(scanId, _brokerId));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM public.exposure WHERE scan_id = '{scanId}'"));

        Assert.Equal(1, await LegCountAsync(scanId, _brokerId, "candidates_found"));
        Assert.Equal(0, await LegCountAsync(scanId, _brokerId, "candidates_recorded"));
    }

    [Fact]
    public async Task Several_listings_are_kept_or_dropped_one_at_a_time()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        _searches.With(_brokerId, new StubBrokerSearch(
            Needs(IdentityField.Names, IdentityField.Addresses),
            _ => new SearchResult.Found(
            [
                new SearchCandidate(Listing, [
                    new FieldMatch(IdentityField.Names, MatchStrength.Exact),
                    new FieldMatch(IdentityField.Addresses, MatchStrength.Exact),
                ]),
                new SearchCandidate(OtherListing, [
                    new FieldMatch(IdentityField.Names, MatchStrength.Exact),
                ]),
            ])));

        await RunAsync(account.TenantId, scanId);

        Assert.Equal(2, await LegCountAsync(scanId, _brokerId, "candidates_found"));
        Assert.Equal(1, await LegCountAsync(scanId, _brokerId, "candidates_recorded"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM public.exposure WHERE scan_id = '{scanId}'"));
    }

    [Fact]
    public async Task A_company_that_holds_nothing_is_an_answer_rather_than_a_failure()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        Search(_brokerId, new SearchResult.NothingFound());

        await RunAsync(account.TenantId, scanId);

        Assert.Equal("nothing_found", await OutcomeOfAsync(scanId, _brokerId));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM public.exposure WHERE scan_id = '{scanId}'"));
        Assert.Equal("completed", await ScanStatusOfAsync(scanId));
    }

    /// <summary>
    /// A search's own account of why it could not answer, carried through unchanged.
    /// </summary>
    [Theory]
    [InlineData(SearchFailureReason.Transient, "transient")]
    [InlineData(SearchFailureReason.RateLimited, "rate_limited")]
    [InlineData(SearchFailureReason.PageShapeChanged, "page_shape_changed")]
    [InlineData(SearchFailureReason.Blocked, "blocked")]
    [InlineData(SearchFailureReason.Unsupported, "unsupported")]
    public async Task A_company_that_did_not_answer_says_why(
        SearchFailureReason reason,
        string expected)
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        Search(_brokerId, new SearchResult.Failed(reason, "the selector matched nothing", false));

        await RunAsync(account.TenantId, scanId);

        Assert.Equal(expected, await OutcomeOfAsync(scanId, _brokerId));

        var detail = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT detail FROM public.scan_leg "
            + $"WHERE scan_id = '{scanId}' AND broker_id = '{_brokerId}'");

        Assert.Equal("the selector matched nothing", detail);

        // One company unreached is a run that did not cover its brokers.
        Assert.Equal("failed", await ScanStatusOfAsync(scanId));
    }

    /// <summary>
    /// A search that throws decided nothing, and is not a company having a bad day.
    /// </summary>
    [Fact]
    public async Task A_search_that_throws_is_recorded_as_the_bug_it_is()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        _searches.With(_brokerId, new StubBrokerSearch(
            Needs(IdentityField.Names),
            _ => throw new InvalidOperationException("the parser fell over")));

        await RunAsync(account.TenantId, scanId);

        Assert.Equal("faulted", await OutcomeOfAsync(scanId, _brokerId));

        // The type, and not the message: an exception's text is written by whatever threw
        // it, and this row is held to the same rule as a log line.
        var detail = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT detail FROM public.scan_leg "
            + $"WHERE scan_id = '{scanId}' AND broker_id = '{_brokerId}'");

        Assert.DoesNotContain("the parser fell over", detail!, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A claim the search was not in a position to make ends the leg and writes nothing.
    /// </summary>
    /// <remarks>
    /// The search declares that it needs names, and reports a listing that agreed on a date
    /// of birth — which it never had. Believing it would file a finding whose evidence
    /// cannot have existed.
    /// </remarks>
    [Fact]
    public async Task A_finding_claiming_a_field_the_search_never_held_is_refused()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        _searches.With(_brokerId, new StubBrokerSearch(
            Needs(IdentityField.Names),
            _ => new SearchResult.Found(
            [
                new SearchCandidate(Listing, [
                    new FieldMatch(IdentityField.DateOfBirth, MatchStrength.Exact),
                ]),
            ])));

        await RunAsync(account.TenantId, scanId);

        Assert.Equal("contract_broken", await OutcomeOfAsync(scanId, _brokerId));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM public.exposure WHERE scan_id = '{scanId}'"));
    }

    /// <summary>
    /// A grant that will not open ends the leg, and the search is never even asked.
    /// </summary>
    [Fact]
    public async Task A_leg_whose_grant_is_refused_never_reaches_the_company()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        var search = new StubBrokerSearch(
            Needs(IdentityField.Names),
            _ => new SearchResult.NothingFound());

        _searches.With(_brokerId, search);

        await RunAsync(account.TenantId, scanId, DirectReleaseClient.Refusing());

        Assert.Equal("release_refused", await OutcomeOfAsync(scanId, _brokerId));
        Assert.Null(search.LastContext);
    }

    /// <summary>
    /// Single-use, seen from the leg: a second delivery of one message changes nothing.
    /// </summary>
    /// <remarks>
    /// The transport is entitled to deliver twice, and the grant is spent by the first
    /// attempt. Without the guard the repeat would record a refused release over a leg that
    /// had already answered — turning a successful run into a failed one because a message
    /// arrived twice.
    /// </remarks>
    [Fact]
    public async Task A_message_delivered_twice_does_not_undo_the_answer()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        Search(_brokerId, Found(
            new FieldMatch(IdentityField.Names, MatchStrength.Exact),
            new FieldMatch(IdentityField.Addresses, MatchStrength.Exact)));

        var work = await DispatchAsync(account.TenantId, scanId);

        await HandleAsync(work.Single());
        await HandleAsync(work.Single());

        Assert.Equal("found", await OutcomeOfAsync(scanId, _brokerId));

        // And the finding is not filed twice.
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM public.exposure WHERE scan_id = '{scanId}'"));
    }

    /// <summary>
    /// A search is handed the groups its grant covered, and no others.
    /// </summary>
    /// <remarks>
    /// The profile has a name, an address, a contact and a date of birth. The search
    /// declares one group, so one group is what crosses the edge — not because the rest
    /// were decrypted and dropped, but because the grant never covered them.
    /// </remarks>
    [Fact]
    public async Task A_search_receives_only_what_it_declared_it_needs()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        var search = new StubBrokerSearch(
            Needs(IdentityField.Names),
            _ => new SearchResult.NothingFound());

        _searches.With(_brokerId, search);

        await RunAsync(account.TenantId, scanId);

        var identity = search.LastContext!.ReleasedIdentity;

        Assert.Equal(["Alex Whitfield"], identity.Names);
        Assert.Empty(identity.Addresses);
        Assert.Empty(identity.Contacts);
        Assert.Null(identity.DateOfBirth);
    }

    /// <summary>The search is told which company it is looking at, and which run it is for.</summary>
    [Fact]
    public async Task A_search_is_told_the_site_and_the_run()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        var search = new StubBrokerSearch(
            Needs(IdentityField.Names),
            _ => new SearchResult.NothingFound());

        _searches.With(_brokerId, search);

        await RunAsync(account.TenantId, scanId);

        Assert.Equal(scanId, search.LastContext!.ScanId);
        Assert.Equal(_brokerId, search.LastContext.Broker.BrokerId);
        Assert.Equal(BrokerDomain, search.LastContext.Broker.Domain);
        Assert.Equal(1, search.LastContext.AttemptNumber);
    }

    /// <summary>
    /// A run is over when its last company answers, and not before.
    /// </summary>
    [Fact]
    public async Task A_run_stays_under_way_until_every_company_has_answered()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        Search(_brokerId, new SearchResult.NothingFound());
        Search(_otherBrokerId, new SearchResult.NothingFound());

        var work = await DispatchAsync(account.TenantId, scanId);

        Assert.Equal(2, work.Count);

        await HandleAsync(work[0]);
        Assert.Equal("running", await ScanStatusOfAsync(scanId));

        await HandleAsync(work[1]);
        Assert.Equal("completed", await ScanStatusOfAsync(scanId));
    }

    /// <summary>
    /// One company unreached is a run that did not cover its brokers, however the rest went.
    /// </summary>
    [Fact]
    public async Task One_company_that_did_not_answer_is_enough_to_fail_the_run()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        Search(_brokerId, new SearchResult.NothingFound());
        Search(_otherBrokerId, new SearchResult.Failed(SearchFailureReason.Blocked, "a bot wall", false));

        var work = await DispatchAsync(account.TenantId, scanId);

        foreach (var one in work)
        {
            await HandleAsync(one);
        }

        Assert.Equal("failed", await ScanStatusOfAsync(scanId));

        // And the run still recorded the company that did answer.
        Assert.Equal("nothing_found", await OutcomeOfAsync(scanId, _brokerId));
    }

    /// <summary>
    /// A finding is filed against the identity the run named, and the account that owns it.
    /// </summary>
    [Fact]
    public async Task A_finding_belongs_to_the_identity_its_run_was_for()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        Search(_brokerId, Found(
            new FieldMatch(IdentityField.Names, MatchStrength.Exact),
            new FieldMatch(IdentityField.Addresses, MatchStrength.Exact)));

        await RunAsync(account.TenantId, scanId);

        var onScan = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT privacy_profile_id FROM public.scan WHERE id = '{scanId}'");

        var onExposure = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT privacy_profile_id FROM public.exposure WHERE scan_id = '{scanId}'");

        var tenant = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT tenant_id FROM public.exposure WHERE scan_id = '{scanId}'");

        Assert.Equal(onScan, onExposure);
        Assert.Equal(account.TenantId, tenant);
    }

    /// <summary>
    /// Spending the grant is recorded, which is the record of a decryption.
    /// </summary>
    [Fact]
    public async Task Running_a_leg_records_that_its_grant_was_spent()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        Search(_brokerId, new SearchResult.NothingFound());

        await RunAsync(account.TenantId, scanId);

        var spent = await CountAsync(
            $"SELECT count(*) FROM public.identity_release "
            + $"WHERE scan_id = '{scanId}' AND redeemed_at IS NOT NULL");

        Assert.Equal(1, spent);
    }

    // ---------------------------------------------------------------------------------

    private static SearchCapabilities Needs(params IdentityField[] fields) =>
        new(SearchKind.Recipe, fields.ToHashSet());

    private static SearchResult Found(params FieldMatch[] matches) =>
        new SearchResult.Found([new SearchCandidate(Listing, matches)]);

    /// <summary>
    /// A search for this company that needs whatever the answer claims to have matched.
    /// </summary>
    /// <remarks>
    /// Derived from the answer rather than stated separately, because the contract refuses
    /// a finding claiming a field the search never held — so a test wanting to assert
    /// something else would otherwise spend its time getting the declaration right.
    /// </remarks>
    private void Search(Guid brokerId, SearchResult answer)
    {
        var fields = answer is SearchResult.Found found
            ? found.Candidates.SelectMany(candidate => candidate.Matches).Select(match => match.Field)
            : [IdentityField.Names];

        _searches.With(
            brokerId,
            new StubBrokerSearch(
                new SearchCapabilities(SearchKind.Recipe, fields.ToHashSet()),
                _ => answer));
    }

    /// <summary>Dispatches a run and then runs every leg it produced.</summary>
    private async Task RunAsync(Guid tenantId, Guid scanId, IReleaseClient? releases = null)
    {
        foreach (var work in await DispatchAsync(tenantId, scanId))
        {
            await HandleAsync(work, releases);
        }
    }

    private async Task<IReadOnlyList<ScanBrokerWork>> DispatchAsync(Guid tenantId, Guid scanId)
    {
        using var scope = _worker.CreateScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        await scope.ServiceProvider
            .GetRequiredService<IScanDispatcher>()
            .DispatchAsync(scanId, TestContext.Current.CancellationToken);

        return [.. _lanes.Sent.Cast<ScanBrokerWork>()];
    }

    /// <summary>
    /// One leg, in its own scope, exactly as a lane would deliver it.
    /// </summary>
    /// <remarks>
    /// A fresh scope per message rather than a shared one, because that is what the
    /// consumer does — and because the tenant is written once per scope, so a shared one
    /// would refuse the second account's work outright.
    /// </remarks>
    private async Task HandleAsync(ScanBrokerWork work, IReleaseClient? releases = null)
    {
        using var scope = _worker.CreateScope();

        var handler = releases is null
            ? scope.ServiceProvider.GetRequiredService<IBrokerWorkHandler<ScanBrokerWork>>()
            : ActivatorUtilities.CreateInstance<ScanBrokerWorkHandler>(scope.ServiceProvider, releases);

        await handler.HandleAsync(work, TestContext.Current.CancellationToken);
    }

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
        services.AddSingleton<IReleaseClient>(new DirectReleaseClient(RedeemAsync, ReportAsync));

        services.AddScoped<ScanCompletion>();
        services.AddScoped<IScanDispatcher, ScanDispatcher>();
        services.AddScoped<IBrokerWorkHandler<ScanBrokerWork>, ScanBrokerWorkHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The edge, without the edge: a real redemption, shaped the way the route shapes it.
    /// </summary>
    /// <remarks>
    /// The mapping is repeated here rather than reached for, because the route that owns it
    /// lives in the API's own assembly behind an HTTPS listener with mutual TLS — which has
    /// its own tests, and which would add certificates and a socket to a test about what a
    /// leg does with what came back. What is not repeated is the part that matters: the
    /// grant is spent against the real service, so a group the token did not cover is
    /// absent because it was never decrypted.
    /// </remarks>
    /// <summary>
    /// The other half of the edge: recording what the leg found, really.
    /// </summary>
    /// <remarks>
    /// Against the API's own container, so the exposure row and the encrypted listing address
    /// beside it are written by the real reporter, under a real data key from a real key
    /// manager. What the socket would add is covered by the internal-edge tests; what these
    /// are about is that a finding reaches both stores and that the grant is spent once.
    /// </remarks>
    private async Task<ReportFindingsResponse?> ReportAsync(
        string token,
        IReadOnlyList<ReportedListingPayload> listings,
        CancellationToken cancellationToken)
    {
        using var scope = _factory.Services.CreateScope();

        var reported = new List<ReportedListing>();

        foreach (var listing in listings)
        {
            reported.Add(new ReportedListing(
                new Uri(listing.SourceRef, UriKind.Absolute),
                [
                    .. listing.Matches.Select(match => new FieldMatch(
                        IdentityVocabulary.Parse(match.Field)!.Value,
                        Enum.Parse<MatchStrength>(match.Strength, ignoreCase: true))),
                ]));
        }

        var result = await scope.ServiceProvider
            .GetRequiredService<IFindingReporter>()
            .ReportAsync(token, reported, cancellationToken);

        return result.Outcome is ReportFindingsOutcome.Recorded
            ? new ReportFindingsResponse(result.Recorded, result.BelowFloor)
            : null;
    }

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

    private async Task<(string Token, Guid TenantId)> OpenScanningAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"leg-{Guid.NewGuid():N}@example.test", authenticator);
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

    private Task<string?> OutcomeOfAsync(Guid scanId, Guid brokerId) =>
        postgres.QueryAsOwnerAsync<string>(
            $"SELECT outcome FROM public.scan_leg "
            + $"WHERE scan_id = '{scanId}' AND broker_id = '{brokerId}'");

    private Task<string?> ScanStatusOfAsync(Guid scanId) =>
        postgres.QueryAsOwnerAsync<string>($"SELECT status FROM public.scan WHERE id = '{scanId}'");

    private Task<int> LegCountAsync(Guid scanId, Guid brokerId, string column) =>
        postgres.QueryAsOwnerAsync<int>(
            $"SELECT {column} FROM public.scan_leg "
            + $"WHERE scan_id = '{scanId}' AND broker_id = '{brokerId}'");

    private Task<long> CountAsync(string sql) => postgres.QueryAsOwnerAsync<long>(sql);
}
