// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Connectors;
using Dbr.Domain.Mail;
using Dbr.Domain.Messaging;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Removals;
using Dbr.Domain.Search;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.InternalEdge;
using Dbr.Infrastructure.Mail;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Removals;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dbr.Integration.Tests;

/// <summary>
/// The line, walked once, from an account that does not exist to a demand a company has.
/// </summary>
/// <remarks>
/// <para>
/// Every stage of this has tests of its own. What none of them can say is that the stages
/// <i>join</i> — they are wired together by dispatchers, message contracts and a catalog,
/// and two correct components that disagree about the shape between them pass every test in
/// the suite. This is the one test that fails when that happens.
/// </para>
/// <para>
/// <b>What is real here.</b> A real account created over HTTP, a real profile encrypted
/// into the vault under a real key from a real key manager, a real consent check, a real
/// scan dispatched and completed, a real exposure written, a real demand opened through the
/// API, a real grant minted and spent for exactly the groups the wording names, the
/// <i>shipped</i> mailbox recipe and the <i>shipped</i> CCPA wording rather than fixtures
/// written for the test, the real templated connector, the real SMTP sender, and a server
/// that speaks SMTP back to it.
/// </para>
/// <para>
/// <b>What is not.</b> One stand-in, in the same place <see cref="ScanLegExecutionTests"/>
/// puts one: what the company's website says, which is not something a test can have an
/// opinion about. And the queue, which is driven directly rather than through RabbitMQ —
/// the lane's own behaviour is asserted where it lives. Everything between those two is the
/// code that runs in production.
/// </para>
/// <para>
/// The broker is inserted under the id the shipped recipe names, deliberately. It is what
/// makes this test read the same two documents an operator would ship, so a change to the
/// wording or the mailbox that nobody meant to make fails here.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class LineOneAcceptanceTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    /// <summary>The company the shipped <c>email.yaml</c> is written for.</summary>
    private static readonly Guid ReferenceBrokerId =
        Guid.Parse("2f6b1c48-9d3a-4e57-b8a1-0c5e7f9d24b3");

    private const string ScansPath = "/api/v1/scans";
    private const string ConsentPath = "/api/v1/profile/consent";
    private const string ProfilePath = "/api/v1/profile";
    private const string RemovalsPath = "/api/v1/removal-requests";
    private const string MailDomain = "removals.example.test";

    private static readonly Uri Listing = new("https://listings.example.test/profile/1");

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly List<TestAuthenticator> _authenticators = [];
    private readonly StubBrokerSearchRegistry _searches = new();
    private readonly RecordingWorkDispatcher _lanes = new();

    private DbrApiFactory _factory = null!;
    private HttpClient _client = null!;
    private ApiClient _api = null!;
    private ServiceProvider _worker = null!;
    private FixtureSmtpServer _relay = null!;

    private string BrokerDomain => $"acceptance-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);
        _relay = FixtureSmtpServer.Start();

        // The reference company, under the id its recipe names, taking demands by mail and
        // confirmed subject to the act the shipped wording invokes.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (id, name, domain, removal_method, sla_days, active)
                 VALUES ('{ReferenceBrokerId}', 'Acceptance Broker {_suffix}',
                         '{BrokerDomain}', 'email', 45, true);

             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT '{ReferenceBrokerId}', l.id, 'counsel'
                 FROM public.legal_basis l
                 WHERE l.code = 'CCPA' AND l.request_type = 'delete'
                   AND l.residency_scope = 'US-CA';
             """);

        _worker = BuildWorker();
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync();
        await _relay.DisposeAsync();

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
             DELETE FROM public.scan_leg;
             DELETE FROM public.identity_release;
             DELETE FROM public.scan_broker;
             DELETE FROM public.scan;
             DELETE FROM public.consent_record;
             DELETE FROM vault.profile_identity;
             DELETE FROM public.privacy_profile;
             DELETE FROM public.tenant;
             DELETE FROM public.passkey_ceremony;
             DELETE FROM public.broker_legal_basis WHERE broker_id = '{ReferenceBrokerId}';
             DELETE FROM public.broker WHERE id = '{ReferenceBrokerId}';
             """);
    }

    [Fact]
    public async Task Signup_through_to_a_demand_a_company_can_read()
    {
        // ---- somebody opens an account and says what they want done -------------------
        var account = await OpenAccountAsync();

        // ---- a scan finds them on the company's site -----------------------------------
        var scanId = await QueueScanAsync(account.Token);

        FoundOnTheirSite();

        await RunScanAsync(account.TenantId, scanId);

        var exposureId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.exposure WHERE scan_id = '{scanId}'");

        // ---- they ask for it to be removed ---------------------------------------------
        var (openedStatus, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = ReferenceBrokerId, requestType = "delete", exposureId },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, openedStatus);
        var requestId = opened.GetProperty("id").GetGuid();

        // The demand cites the act rather than a courtesy target, which is what selects the
        // statutory wording below rather than the request that asserts nothing.
        Assert.Equal(
            "statutory",
            await postgres.QueryAsOwnerAsync<string>(
                $"SELECT deadline_source FROM public.removal_request WHERE id = '{requestId}'"));

        // ---- and the worker sends it ---------------------------------------------------
        var work = await DispatchAsync(account.TenantId, requestId);
        await HandleAsync(account.TenantId, work);

        // ---- what the company actually received ----------------------------------------
        // Read the attempt first, so that a demand which never left says why rather than
        // failing as an empty collection. The reason is the connector's own, in the words
        // it recorded.
        Assert.True(
            _relay.Messages.Count == 1,
            $"No demand reached the relay. The attempt recorded: {await AttemptDetailAsync()}");

        var sent = Assert.Single(_relay.Messages);

        Assert.Equal($"privacy@{BrokerDomain}", Assert.Single(sent.Recipients));
        Assert.Equal($"removals-{work.RemovalJobId:D}@{MailDomain}", sent.From);
        Assert.Equal("Request to delete personal information (CCPA)", sent.Header("Subject"));

        // The identity reached the message, which means the grant was minted, spent, and
        // decrypted the groups the wording names — the whole vault path, observed from the
        // one place it is finally visible.
        Assert.Contains("Alex Whitfield", sent.Body, StringComparison.Ordinal);
        Assert.Contains("12 Rowan Lane", sent.Body, StringComparison.Ordinal);
        Assert.Contains("alex@example.test", sent.Body, StringComparison.Ordinal);
        Assert.Contains("1798.105", sent.Body, StringComparison.Ordinal);

        // And what it did not receive. The wording names no date of birth, so nothing
        // decrypted one — the declaration and the release agreeing, end to end.
        Assert.DoesNotContain("1985", sent.Body, StringComparison.Ordinal);

        // ---- and what the tenant is now told -------------------------------------------
        Assert.Equal(
            "awaiting_broker_response",
            await postgres.QueryAsOwnerAsync<string>(
                $"SELECT status FROM public.removal_request WHERE id = '{requestId}'"));

        var (timelineStatus, timeline) = await _api.GetAsync(
            $"{RemovalsPath}/{requestId}/timeline", account.Token);

        Assert.Equal(HttpStatusCode.OK, timelineStatus);

        // One attempt, recorded under the connector that made it — which is the answer to
        // "what was actually done on my behalf", asked of the API rather than the database.
        var attempt = Assert.Single(timeline.GetProperty("attempts").EnumerateArray().ToList());

        Assert.StartsWith("email.", attempt.GetProperty("connectorId").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relay_that_refuses_leaves_the_demand_to_be_tried_again()
    {
        // The other half of the line: a demand that could not be handed over is not a demand
        // that was made. Nothing should record it as sent, and the request should be back
        // where the dispatcher will find it.
        var account = await OpenAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        FoundOnTheirSite();

        await RunScanAsync(account.TenantId, scanId);

        var exposureId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.exposure WHERE scan_id = '{scanId}'");

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = ReferenceBrokerId, requestType = "delete", exposureId },
            account.Token);

        var requestId = opened.GetProperty("id").GetGuid();

        _relay.RefuseWith = 451;

        var work = await DispatchAsync(account.TenantId, requestId);
        await HandleAsync(account.TenantId, work);

        Assert.Empty(_relay.Messages);

        Assert.Equal(
            "queued",
            await postgres.QueryAsOwnerAsync<string>(
                $"SELECT status FROM public.removal_request WHERE id = '{requestId}'"));
    }

    /// <summary>
    /// The company's site says this person is listed, on a name and an address.
    /// </summary>
    /// <remarks>
    /// The declaration is derived from what the answer claims to have matched, because the
    /// contract refuses a finding naming a field the search never held — stating the two
    /// separately would make this test about getting that agreement right.
    /// </remarks>
    private void FoundOnTheirSite()
    {
        FieldMatch[] matches =
        [
            new(IdentityField.Names, MatchStrength.Exact),
            new(IdentityField.Addresses, MatchStrength.Exact),
        ];

        _searches.With(
            ReferenceBrokerId,
            new StubBrokerSearch(
                new SearchCapabilities(
                    SearchKind.Recipe,
                    matches.Select(match => match.Field).ToHashSet()),
                _ => new SearchResult.Found([new SearchCandidate(Listing, matches)])));
    }

    /// <summary>What the attempt says about itself, for a failure message worth reading.</summary>
    private async Task<string?> AttemptDetailAsync() =>
        await postgres.QueryAsOwnerAsync<string?>(
            """
            SELECT coalesce(status, '?') || ' / ' || coalesce(failure_reason, 'no reason')
                   || ' / ' || coalesce(detail, 'no detail')
            FROM public.removal_job
            ORDER BY run_at DESC
            LIMIT 1
            """);

    private async Task<(string Token, Guid TenantId)> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"line1-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        foreach (var scope in new[] { "scan", "auto_removal" })
        {
            await _api.PostAsync(
                ConsentPath,
                new { scope, granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
                token);
        }

        // California, because that is what makes the CCPA wording the right wording. The
        // residency is coarse and lives outside the vault; the name and address below do not.
        await _api.PutAsync(
            ProfilePath,
            new
            {
                names = new[] { "Alex Whitfield" },
                dateOfBirth = "1985-04-17",
                residencyRegion = "US-CA",
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

        return (token, ApiClient.TenantId(session));
    }

    private async Task<Guid> QueueScanAsync(string token)
    {
        var (status, scan) = await _api.PostAsync(
            ScansPath, new { brokerIds = new[] { ReferenceBrokerId } }, token);

        Assert.Equal(HttpStatusCode.Accepted, status);

        return scan.GetProperty("id").GetGuid();
    }

    private async Task RunScanAsync(Guid tenantId, Guid scanId)
    {
        using var scope = _worker.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        await scope.ServiceProvider
            .GetRequiredService<IScanDispatcher>()
            .DispatchAsync(scanId, CancellationToken.None);

        foreach (var work in _lanes.Sent.OfType<ScanBrokerWork>().ToList())
        {
            using var handling = _worker.CreateScope();
            handling.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

            await handling.ServiceProvider
                .GetRequiredService<IBrokerWorkHandler<ScanBrokerWork>>()
                .HandleAsync(work, CancellationToken.None);
        }
    }

    private async Task<RemovalJobWork> DispatchAsync(Guid tenantId, Guid requestId)
    {
        using var scope = _worker.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        await scope.ServiceProvider
            .GetRequiredService<IRemovalDispatcher>()
            .DispatchAsync(requestId, CancellationToken.None);

        return _lanes.Sent.OfType<RemovalJobWork>().Single();
    }

    private async Task HandleAsync(Guid tenantId, RemovalJobWork work)
    {
        using var scope = _worker.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        await scope.ServiceProvider
            .GetRequiredService<RemovalJobWorkHandler>()
            .HandleAsync(work, CancellationToken.None);
    }

    /// <summary>
    /// The worker's container: no vault, no key manager, and the real connectors.
    /// </summary>
    private ServiceProvider BuildWorker()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Core"] = postgres.ConnectionString,
                ["Mail:Host"] = "127.0.0.1",
                ["Mail:Port"] = _relay.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mail:Username"] = "dbr",
                ["Mail:Password"] = "secret",
                ["Mail:Domain"] = MailDomain,
                ["Mail:RequireTls"] = "false",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbrPersistence(configuration);
        services.AddDbrReleaseMinting(configuration);

        // The two registrations this test exists to exercise, wired exactly as the worker's
        // own composition root wires them — including reading the shipped catalog off disk
        // and refusing to start if it cannot be used.
        services.AddDbrMail(configuration);
        services.AddDbrEmailConnectors();

        services.AddSingleton<IBrokerSearchRegistry>(_searches);
        services.AddSingleton<IBrokerWorkDispatcher>(_lanes);
        services.AddSingleton<IReleaseClient>(new DirectReleaseClient(RedeemAsync, ReportAsync));
        services.AddSingleton<IQueuedRemovalDirectory>(
            new QueuedRemovalDirectory(postgres.ConnectionString));
        services.AddSingleton(Options.Create(new RemovalOptions()));
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<RemovalVerification>();
        services.AddScoped<ScanCompletion>();
        services.AddScoped<IScanDispatcher, ScanDispatcher>();
        services.AddScoped<IBrokerWorkHandler<ScanBrokerWork>, ScanBrokerWorkHandler>();
        services.AddScoped<IRemovalDispatcher, RemovalDispatcher>();
        services.AddScoped<RemovalJobWorkHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The edge without the socket: a real redemption, shaped the way the route shapes it.
    /// </summary>
    /// <remarks>
    /// The same stand-in <see cref="ScanLegExecutionTests"/> uses, and for the same reason —
    /// the route that owns this mapping sits behind mutual TLS and has its own tests, and
    /// putting certificates in front of this would be testing the handshake rather than the
    /// line. The part that is not stood in for is the part that matters: the grant is spent
    /// against the real service, so a group the token did not cover is absent from the
    /// message below because it was never decrypted.
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

    private async Task<ReportFindingsResponse?> ReportAsync(
        string token,
        IReadOnlyList<ReportedListingPayload> listings,
        CancellationToken cancellationToken)
    {
        using var scope = _factory.Services.CreateScope();

        var reported = listings
            .Select(listing => new ReportedListing(
                new Uri(listing.SourceRef, UriKind.Absolute),
                [
                    .. listing.Matches.Select(match => new FieldMatch(
                        IdentityVocabulary.Parse(match.Field)!.Value,
                        Enum.Parse<MatchStrength>(match.Strength, ignoreCase: true))),
                ]))
            .ToList();

        var result = await scope.ServiceProvider
            .GetRequiredService<IFindingReporter>()
            .ReportAsync(token, reported, cancellationToken);

        return result.Outcome is ReportFindingsOutcome.Recorded
            ? new ReportFindingsResponse(result.Recorded, result.BelowFloor)
            : null;
    }
}
