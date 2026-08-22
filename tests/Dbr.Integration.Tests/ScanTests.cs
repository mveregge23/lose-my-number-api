// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Integration.Tests.Fixtures;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Asking for a scan: what it takes, what it refuses, and what the database refuses
/// underneath it.
/// </summary>
/// <remarks>
/// <para>
/// Two claims here are properties of the schema rather than of the code, and neither
/// survives a test against an in-memory provider: that a scan cannot be made to point at
/// another account's profile, and that an exposure cannot be made to hang off another
/// account's scan. Row-level security does not give either one — Postgres checks a
/// foreign key with row security off — so they are asserted by trying the write as
/// <c>dbr_app</c> and watching the constraint reject it.
/// </para>
/// <para>
/// The rest is over HTTP, because the consent gate and the tenant boundary both depend on
/// what the token established.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ScanTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _brokerId;

    private Guid _otherBrokerId;

    private string BrokerDomain => $"scan-broker-{_suffix}.test";

    private string OtherBrokerDomain => $"scan-other-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Scan Broker {_suffix}', '{BrokerDomain}', 'webform', 45, true),
                        ('Other Broker {_suffix}', '{OtherBrokerDomain}', 'email', 30, true);
             """);

        _brokerId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");
        _otherBrokerId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{OtherBrokerDomain}'");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        // Children before parents throughout: exposures and narrowings reference a scan,
        // a scan references a profile and a tenant, and none of it cascades from the
        // account on purpose.
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

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Every_scan_route_refuses_a_request_with_no_token(string method)
    {
        var (status, _) = method == "GET"
            ? await _api.GetAsync(ScansPath, null)
            : await _api.PostAsync(ScansPath, new { }, null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task An_account_that_has_not_permitted_scanning_is_refused_and_nothing_is_written()
    {
        // The switch is not decorative. Somebody who wanted to see the catalog without
        // anything being done in their name said exactly that, and this is the request
        // that has to honour it.
        var (token, tenantId) = await OpenAccountAsync();

        var (status, problem) = await _api.PostAsync(ScansPath, new { }, token);

        // Not 401: the caller is who they claim to be, and a fresh token would not help.
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("permitted", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        Assert.Equal(0L, await ScanCountAsync(tenantId));
    }

    [Fact]
    public async Task Withdrawing_permission_stops_the_next_scan()
    {
        // The case a check cached at signup would get wrong, in the direction of
        // searching for somebody who has since told us to stop.
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token, true);

        var (allowed, _) = await _api.PostAsync(ScansPath, new { }, token);
        Assert.Equal(HttpStatusCode.Accepted, allowed);

        await GrantScanAsync(token, false);

        var (refused, _) = await _api.PostAsync(ScansPath, new { }, token);
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        // The one from before the withdrawal stays. It was permitted when it was asked
        // for, and that is what the consent history exists to be able to say.
        Assert.Equal(1L, await ScanCountAsync(tenantId));
    }

    [Fact]
    public async Task A_scan_with_nothing_named_is_queued_against_the_accounts_own_identity()
    {
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token, true);

        var (status, body) = await _api.PostAsync(ScansPath, new { }, token);

        // Accepted rather than Created: the run has been taken on and has not happened.
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.Equal("manual", body.GetProperty("trigger").GetString());
        Assert.Equal(await SelfProfileIdAsync(tenantId), body.GetProperty("profileId").GetGuid());

        // Not started and not finished, reported as null rather than as a zero date a
        // client would render as 1970.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("completedAt").ValueKind);

        // No narrowing means the whole catalog, which is no rows rather than one per
        // broker — the scope is resolved when the run happens, so a broker added in
        // between is one the tenant meant to include.
        Assert.Empty(body.GetProperty("brokerIds").EnumerateArray());
        Assert.Equal(0L, await NarrowingCountAsync(tenantId));
    }

    [Fact]
    public async Task Naming_another_accounts_profile_answers_the_same_as_naming_none()
    {
        // Telling "not yours" apart from "does not exist" would confirm that an id is in
        // use on another account, which is the one thing an id-probing client wants.
        var (mine, _) = await OpenAccountAsync();
        var (_, theirTenantId) = await OpenAccountAsync();

        await GrantScanAsync(mine, true);

        var theirProfileId = await SelfProfileIdAsync(theirTenantId);

        var (status, _) = await _api.PostAsync(ScansPath, new { profileId = theirProfileId }, mine);

        Assert.Equal(HttpStatusCode.NotFound, status);

        var (missing, _) = await _api.PostAsync(ScansPath, new { profileId = Guid.NewGuid() }, mine);

        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task The_database_refuses_a_scan_pointing_at_another_tenants_profile()
    {
        // The guarantee the endpoint's 404 is only the polite half of. Row-level security
        // does not cover this: Postgres validates a foreign key with row security off, so
        // an id alone would have referenced any profile in the table. The key is over the
        // tenant and the profile together, which is what makes the cross-tenant row
        // impossible rather than merely unreachable.
        var (_, mineTenantId) = await OpenAccountAsync();
        var (_, theirTenantId) = await OpenAccountAsync();

        var theirProfileId = await SelfProfileIdAsync(theirTenantId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_app;
                 SELECT set_config('app.tenant_id', '{mineTenantId}', false);
                 INSERT INTO public.scan (tenant_id, privacy_profile_id, trigger, status)
                     VALUES ('{mineTenantId}', '{theirProfileId}', 'manual', 'queued');
                 """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task The_database_refuses_an_exposure_hung_off_another_tenants_scan()
    {
        // The same shape one level down. Without it, a finding could be attached to
        // somebody else's run, and the run's owner would never see the row that claims to
        // belong to it.
        var (token, mineTenantId) = await OpenAccountAsync();
        var (_, theirTenantId) = await OpenAccountAsync();

        await GrantScanAsync(token, true);
        var (_, body) = await _api.PostAsync(ScansPath, new { }, token);
        var myScanId = body.GetProperty("id").GetGuid();

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_app;
                 SELECT set_config('app.tenant_id', '{theirTenantId}', false);
                 INSERT INTO public.exposure (tenant_id, scan_id, broker_id, status, confidence)
                     VALUES ('{theirTenantId}', '{myScanId}', '{_brokerId}', 'new', 0.9);
                 """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);

        // And the scan really is mine, so the rejection is about the pairing rather than
        // about the scan not existing.
        Assert.Equal(1L, await ScanCountAsync(mineTenantId));
    }

    [Fact]
    public async Task An_unknown_broker_is_refused_with_every_bad_id_named_and_nothing_written()
    {
        // Trimming to the ones that exist would run a smaller scan than was asked for and
        // report it as the one that was asked for. Every id rather than the first,
        // because somebody fixing a request wants the whole list.
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token, true);

        var firstMissing = Guid.NewGuid();
        var secondMissing = Guid.NewGuid();

        var (status, problem) = await _api.PostAsync(
            ScansPath,
            new { brokerIds = new[] { _brokerId, firstMissing, secondMissing } },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, status);

        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains(firstMissing.ToString(), detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondMissing.ToString(), detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0L, await ScanCountAsync(tenantId));
        Assert.Equal(0L, await NarrowingCountAsync(tenantId));
    }

    [Fact]
    public async Task Narrowing_records_the_brokers_and_reads_them_back()
    {
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token, true);

        // Named twice on purpose: a repeated id is a clumsy client rather than an error,
        // and it must not become two rows the key would reject.
        var (status, body) = await _api.PostAsync(
            ScansPath,
            new { brokerIds = new[] { _brokerId, _otherBrokerId, _brokerId } },
            token);

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(2L, await NarrowingCountAsync(tenantId));

        var scanId = body.GetProperty("id").GetGuid();
        var (found, detail) = await _api.GetAsync($"{ScansPath}/{scanId}", token);

        Assert.Equal(HttpStatusCode.OK, found);

        Assert.Equal(
            new HashSet<Guid> { _brokerId, _otherBrokerId },
            detail.GetProperty("brokerIds").EnumerateArray().Select(id => id.GetGuid()).ToHashSet());
    }

    [Fact]
    public async Task History_holds_this_accounts_scans_newest_first_and_nobody_elses()
    {
        var (mine, _) = await OpenAccountAsync();
        var (theirs, _) = await OpenAccountAsync();

        await GrantScanAsync(mine, true);
        await GrantScanAsync(theirs, true);

        var (_, first) = await _api.PostAsync(ScansPath, new { }, mine);
        var (_, second) = await _api.PostAsync(ScansPath, new { }, mine);
        await _api.PostAsync(ScansPath, new { }, theirs);

        var (status, body) = await _api.GetAsync(ScansPath, mine);

        Assert.Equal(HttpStatusCode.OK, status);

        var ids = body.GetProperty("scans")
            .EnumerateArray()
            .Select(scan => scan.GetProperty("id").GetGuid())
            .ToList();

        // Two, not three — the other account's run is not an error here, it is simply
        // absent, which is what catches a route resolving scans from anything but the
        // token.
        Assert.Equal(
            [second.GetProperty("id").GetGuid(), first.GetProperty("id").GetGuid()],
            ids);
    }

    [Fact]
    public async Task One_accounts_scan_is_not_readable_by_another()
    {
        var (mine, _) = await OpenAccountAsync();
        var (theirs, _) = await OpenAccountAsync();

        await GrantScanAsync(mine, true);

        var (_, body) = await _api.PostAsync(ScansPath, new { }, mine);
        var scanId = body.GetProperty("id").GetGuid();

        var (status, _) = await _api.GetAsync($"{ScansPath}/{scanId}", theirs);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("scan_broker")]
    [InlineData("exposure")]
    public async Task Every_new_table_is_inside_the_tenant_boundary(string table)
    {
        // The inverse of the catalog's assertion. These hold somebody's data, so the
        // failure to catch is a table that was added without opting in — which looks
        // exactly like a working table until two accounts share an instance.
        var enabled = await postgres.QueryAsOwnerAsync<bool>(
            $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class "
            + $"WHERE oid = 'public.{table}'::regclass");

        Assert.True(enabled);
    }

    private async Task<Guid> SelfProfileIdAsync(Guid tenantId) =>
        await IdOfAsync(
            $"SELECT id FROM public.privacy_profile WHERE tenant_id = '{tenantId}' "
            + "AND relationship_type = 'self'");

    private async Task<long> ScanCountAsync(Guid tenantId) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.scan WHERE tenant_id = '{tenantId}'");

    private async Task<long> NarrowingCountAsync(Guid tenantId) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.scan_broker WHERE tenant_id = '{tenantId}'");

    private async Task<Guid> IdOfAsync(string sql) => await postgres.QueryAsOwnerAsync<Guid>(sql);

    private async Task<HttpStatusCode> GrantScanAsync(string token, bool granted)
    {
        var (status, _) = await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        return status;
    }

    private async Task<(string AccessToken, Guid TenantId)> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"scan-{Guid.NewGuid():N}@example.test", authenticator);

        return (ApiClient.AccessToken(session), ApiClient.TenantId(session));
    }
}
