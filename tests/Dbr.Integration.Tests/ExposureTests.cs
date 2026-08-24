// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Reading findings, filtering them, and saying which of them are not you.
/// </summary>
/// <remarks>
/// Nothing writes an exposure yet — there is no scan worker — so these seed rows the way
/// one eventually will: against a real scan, owned by a real account, through the owning
/// role. That is deliberate rather than a shortcut. Going through the API would only test
/// the API against itself, and the interesting claims here are about what one account can
/// see of another's and about a state change a person makes.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ExposureTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ExposuresPath = "/api/v1/exposures";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _acmeId;

    private Guid _otherId;

    private string AcmeDomain => $"exp-acme-{_suffix}.test";

    private string OtherDomain => $"exp-other-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Exp Acme {_suffix}', '{AcmeDomain}', 'webform', 45, true),
                        ('Exp Other {_suffix}', '{OtherDomain}', 'email', 30, true);
             """);

        _acmeId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{AcmeDomain}'");
        _otherId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{OtherDomain}'");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        await postgres.ExecuteAsOwnerAsync(
            $"""
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

    [Fact]
    public async Task Every_exposure_route_refuses_a_request_with_no_token()
    {
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(ExposuresPath, null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync($"{ExposuresPath}/{id}", null)).Status);

        var (dismiss, _) = await _api.PostAsync($"{ExposuresPath}/{id}/dismiss", new { }, null);
        Assert.Equal(HttpStatusCode.Unauthorized, dismiss);
    }

    [Fact]
    public async Task A_finding_arrives_with_the_company_it_is_on()
    {
        // A listing is unreadable without knowing whose site it is on, and a client made
        // to resolve ids against the catalog will either show somebody a uuid or hold the
        // whole list back on a second call.
        var account = await OpenAccountAsync();
        await SeedAsync(account, _acmeId, "new", 0.91);

        var (status, body) = await _api.GetAsync(ExposuresPath, account.Token);

        Assert.Equal(HttpStatusCode.OK, status);

        var finding = Assert.Single(body.GetProperty("exposures").EnumerateArray());
        Assert.Equal("new", finding.GetProperty("status").GetString());
        Assert.Equal(0.91, finding.GetProperty("confidence").GetDouble(), 3);

        var broker = finding.GetProperty("broker");
        Assert.Equal(_acmeId, broker.GetProperty("id").GetGuid());
        Assert.Equal(AcmeDomain, broker.GetProperty("domain").GetString());
        Assert.Equal("webform", broker.GetProperty("removalMethod").GetString());

        // Nothing has looked again since it was found, which is not the same as having
        // been confirmed present just now.
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("lastVerifiedAt").ValueKind);
    }

    [Fact]
    public async Task The_pacing_fields_do_not_reach_the_wire_here_either()
    {
        // The same line the catalog routes draw. How this instance decides to talk to a
        // company is not part of what somebody was found on, and the exact number of
        // refusals that stops it trying is only useful to whoever wants it to stop.
        var account = await OpenAccountAsync();
        await SeedAsync(account, _acmeId, "new", 0.5);

        var (_, body) = await _api.GetAsync(ExposuresPath, account.Token);
        var broker = body.GetProperty("exposures").EnumerateArray().Single().GetProperty("broker");

        foreach (var withheld in new[]
                 {
                     "maxConcurrency", "minDelayMs", "rateLimitThreshold", "cooldownMinutes",
                     "formChangeThreshold",
                 })
        {
            Assert.False(broker.TryGetProperty(withheld, out _), $"{withheld} reached the wire");
        }
    }

    [Fact]
    public async Task Findings_come_back_newest_first()
    {
        var account = await OpenAccountAsync();

        var older = await SeedAsync(account, _acmeId, "new", 0.4, "now() - interval '3 days'");
        var newer = await SeedAsync(account, _otherId, "new", 0.6, "now() - interval '1 hour'");

        var (_, body) = await _api.GetAsync(ExposuresPath, account.Token);

        Assert.Equal(
            [newer, older],
            body.GetProperty("exposures").EnumerateArray()
                .Select(finding => finding.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task One_accounts_findings_are_invisible_to_another()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var myFinding = await SeedAsync(mine, _acmeId, "new", 0.8);

        var (listStatus, list) = await _api.GetAsync(ExposuresPath, theirs.Token);

        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Empty(list.GetProperty("exposures").EnumerateArray());

        // And not reachable by naming it either, which answers the same as one that was
        // never created — telling those apart would confirm the id is in use elsewhere.
        var (detail, _) = await _api.GetAsync($"{ExposuresPath}/{myFinding}", theirs.Token);
        Assert.Equal(HttpStatusCode.NotFound, detail);
    }

    [Fact]
    public async Task Filtering_by_status_narrows_to_that_state()
    {
        var account = await OpenAccountAsync();

        var outstanding = await SeedAsync(account, _acmeId, "new", 0.7);
        await SeedAsync(account, _otherId, "removed", 0.7);

        var (status, body) = await _api.GetAsync($"{ExposuresPath}?status=new", account.Token);

        Assert.Equal(HttpStatusCode.OK, status);

        var finding = Assert.Single(body.GetProperty("exposures").EnumerateArray());
        Assert.Equal(outstanding, finding.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Filtering_by_broker_narrows_to_that_company()
    {
        var account = await OpenAccountAsync();

        await SeedAsync(account, _acmeId, "new", 0.7);
        var onOther = await SeedAsync(account, _otherId, "new", 0.7);

        var (_, body) = await _api.GetAsync($"{ExposuresPath}?brokerId={_otherId}", account.Token);

        var finding = Assert.Single(body.GetProperty("exposures").EnumerateArray());
        Assert.Equal(onOther, finding.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_unrecognised_status_is_refused_rather_than_answered_with_an_empty_list()
    {
        // The reason this is worth an integration test and not only a unit one: the wrong
        // behaviour here is a 200 with an empty array, which reads as "you are not listed
        // anywhere" — a sentence somebody would act on.
        var account = await OpenAccountAsync();
        await SeedAsync(account, _acmeId, "new", 0.7);

        var (status, problem) = await _api.GetAsync($"{ExposuresPath}?status=pending", account.Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("'new'", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dismissing_marks_it_and_saying_so_twice_answers_the_same()
    {
        var account = await OpenAccountAsync();
        var finding = await SeedAsync(account, _acmeId, "new", 0.3);

        var (first, body) = await _api.PostAsync($"{ExposuresPath}/{finding}/dismiss", new { }, account.Token);

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal("dismissed", body.GetProperty("status").GetString());

        var (second, repeated) = await _api.PostAsync($"{ExposuresPath}/{finding}/dismiss", new { }, account.Token);

        // A retry or a double-tapped button. The client asked for a state and the state
        // holds; which call put it there is not what it asked about.
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Equal("dismissed", repeated.GetProperty("status").GetString());

        Assert.Equal("dismissed", await StatusOfAsync(finding));
    }

    [Fact]
    public async Task A_dismissed_finding_stays_out_of_the_working_list_until_asked_for()
    {
        var account = await OpenAccountAsync();
        var finding = await SeedAsync(account, _acmeId, "new", 0.3);

        await _api.PostAsync($"{ExposuresPath}/{finding}/dismiss", new { }, account.Token);

        var (_, outstanding) = await _api.GetAsync($"{ExposuresPath}?status=new", account.Token);
        Assert.Empty(outstanding.GetProperty("exposures").EnumerateArray());

        // Still there, and still theirs. Dismissing is a judgement, not a delete — the row
        // is what stops a later scan re-offering the same listing as a new discovery.
        var (_, dismissed) = await _api.GetAsync($"{ExposuresPath}?status=dismissed", account.Token);
        Assert.Single(dismissed.GetProperty("exposures").EnumerateArray());
    }

    [Fact]
    public async Task A_finding_with_a_removal_in_flight_cannot_be_dismissed()
    {
        // Dismissing means "this is not me". A request already sent in somebody's name
        // over a listing they now disown is not resolved by writing a status column; it is
        // resolved by cancelling the request, which has consequences at the broker.
        var account = await OpenAccountAsync();
        var finding = await SeedAsync(account, _acmeId, "requested", 0.9);

        var (status, problem) = await _api.PostAsync(
            $"{ExposuresPath}/{finding}/dismiss",
            new { },
            account.Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("cancel the request", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        // Unchanged, rather than refused after having been written.
        Assert.Equal("requested", await StatusOfAsync(finding));
    }

    [Fact]
    public async Task Dismissing_somebody_elses_finding_answers_not_found_and_leaves_it_alone()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var theirFinding = await SeedAsync(theirs, _acmeId, "new", 0.8);

        var (status, _) = await _api.PostAsync(
            $"{ExposuresPath}/{theirFinding}/dismiss",
            new { },
            mine.Token);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("new", await StatusOfAsync(theirFinding));
    }

    private async Task<string?> StatusOfAsync(Guid exposureId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.exposure WHERE id = '{exposureId}'");

    /// <summary>
    /// Writes one finding against the account's real scan, the way a worker will.
    /// </summary>
    private async Task<Guid> SeedAsync(
        Account account,
        Guid brokerId,
        string status,
        double confidence,
        string discoveredAt = "now()")
    {
        var id = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.exposure
                 (id, tenant_id, scan_id, broker_id, status, confidence, discovered_at)
                 VALUES ('{id}', '{account.TenantId}', '{account.ScanId}', '{brokerId}',
                         '{status}', {confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                         {discoveredAt});
             """);

        return id;
    }

    private async Task<Account> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"exp-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        // A real scan, so the findings hang off one the way the composite key requires.
        var (_, scan) = await _api.PostAsync(ScansPath, new { }, token);

        return new Account(token, ApiClient.TenantId(session), scan.GetProperty("id").GetGuid());
    }

    private sealed record Account(string Token, Guid TenantId, Guid ScanId);
}
