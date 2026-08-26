// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Integration.Tests.Fixtures;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// What the schema will and will not hold for a removal request.
/// </summary>
/// <remarks>
/// Nothing creates removal requests yet — that is the API story — so these write rows the
/// way it will, as <c>dbr_app</c> with a tenant established, and check what the database
/// refuses. Every claim here is a property of the schema: the composite keys, the
/// statutory-basis pairing, one open demand per listing, one job per attempt.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class RemovalRequestTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _brokerId;

    private Guid _otherBrokerId;

    private Guid _basisId;

    private string BrokerDomain => $"rem-acme-{_suffix}.test";

    private string OtherBrokerDomain => $"rem-other-{_suffix}.test";

    private string BasisCode => $"R{_suffix}";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Rem Acme {_suffix}', '{BrokerDomain}', 'webform', 45, true),
                        ('Rem Other {_suffix}', '{OtherBrokerDomain}', 'email', 30, true);

             INSERT INTO public.legal_basis
                 (code, request_type, residency_scope, response_deadline_days, extension_days,
                  verification_level, citation_url, reviewed_at, reviewed_by)
                 VALUES ('{BasisCode}', 'delete', 'US-CA', 45, 45, 'basic',
                         'https://example.test/{BasisCode}', now(), 'counsel');
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");
        _otherBrokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{OtherBrokerDomain}'");
        _basisId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.legal_basis WHERE code = '{BasisCode}'");
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
             DELETE FROM public.removal_job;
             DELETE FROM public.removal_request;
             DELETE FROM public.exposure;
             DELETE FROM public.scan_broker;
             DELETE FROM public.scan;
             DELETE FROM public.consent_record;
             DELETE FROM vault.profile_identity;
             DELETE FROM public.privacy_profile;
             DELETE FROM public.tenant;
             DELETE FROM public.passkey_ceremony;
             DELETE FROM public.legal_basis WHERE code = '{BasisCode}';
             DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';
             """);
    }

    [Theory]
    [InlineData("removal_request")]
    [InlineData("removal_job")]
    public async Task Both_tables_are_inside_the_tenant_boundary(string table)
    {
        var enabled = await postgres.QueryAsOwnerAsync<bool>(
            $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class "
            + $"WHERE oid = 'public.{table}'::regclass");

        Assert.True(enabled);
    }

    [Fact]
    public async Task A_request_can_be_opened_against_a_listing()
    {
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        await InsertRequestAsync(account, exposure, _brokerId);

        Assert.Equal(1L, await RequestCountAsync(account.TenantId));
    }

    [Fact]
    public async Task A_request_cannot_be_attached_to_another_accounts_listing()
    {
        // The rule every tenant-scoped child here carries. What it prevents is a demand
        // sent in one person's name about another person's listing.
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var theirExposure = await SeedExposureAsync(theirs, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(mine, theirExposure, _brokerId));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_request_cannot_name_a_broker_the_listing_was_not_found_on()
    {
        // The new one. A removal request carries its broker so the dispatcher can route
        // without a join, and that duplication is a chance for the two to disagree — with
        // the consequence that a demand goes to a company that never listed this person.
        // The key is over the exposure and its broker together, so they cannot.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, exposure, _otherBrokerId));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_statutory_deadline_needs_a_regime_behind_it()
    {
        // A statutory claim with nothing to check it against is a promise about somebody's
        // legal position with no citation. Refused rather than stored.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, exposure, _brokerId, deadlineSource: "statutory", basisId: null));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_courtesy_deadline_cannot_carry_a_regime()
    {
        // The other direction, and the one that would misdescribe what somebody has. A
        // regime recorded against the broker's own target reads as a legal deadline.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(
                account, exposure, _brokerId, deadlineSource: "operational_default", basisId: _basisId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_statutory_request_records_which_regime_governed()
    {
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        await InsertRequestAsync(account, exposure, _brokerId, deadlineSource: "statutory", basisId: _basisId);

        var recorded = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT legal_basis_id FROM public.removal_request WHERE tenant_id = '{account.TenantId}'");

        Assert.Equal(_basisId, recorded);
    }

    [Fact]
    public async Task Only_one_open_demand_per_identity_company_and_kind()
    {
        // The rule used to be per listing, which cannot state itself once a demand may
        // cite no listing. What it was really protecting against is telling one company
        // the same thing twice about the same person.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        await InsertRequestAsync(account, exposure, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, exposure, _brokerId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task Two_demands_with_no_listing_collide_the_same_way()
    {
        // The case the old per-exposure index could not see at all: two null exposure ids
        // are distinct to a unique index, so keying on the listing would have let a
        // blanket demand go out twice over.
        var account = await OpenAccountAsync();

        await InsertRequestAsync(account, exposureId: null, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, exposureId: null, _brokerId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_deletion_and_an_opt_out_may_be_open_at_once()
    {
        // Different rights, not duplicate demands. Some brokers answer one and not the
        // other, and a person is entitled to ask for both.
        var account = await OpenAccountAsync();

        await InsertRequestAsync(account, exposureId: null, _brokerId, requestType: "delete");
        await InsertRequestAsync(account, exposureId: null, _brokerId, requestType: "opt_out_sale");

        Assert.Equal(2L, await RequestCountAsync(account.TenantId));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public async Task A_dead_demand_does_not_block_a_fresh_one(string deadStatus)
    {
        // Retries ran out, or the person called it off. Either way the listing may still
        // be there, and they must be able to try again.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        await InsertRequestAsync(account, exposure, _brokerId, status: deadStatus);
        await InsertRequestAsync(account, exposure, _brokerId);

        Assert.Equal(2L, await RequestCountAsync(account.TenantId));
    }

    [Fact]
    public async Task A_removed_demand_still_blocks_a_second_one()
    {
        // Not an oversight in the index predicate. A listing that comes back reappears on
        // the request that removed it, so that request is still the one for this exposure.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);

        await InsertRequestAsync(account, exposure, _brokerId, status: "removed");

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, exposure, _brokerId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task One_job_per_attempt()
    {
        // A second row claiming to be attempt three makes a request's history unreadable,
        // in exactly the case somebody is reading it because things went wrong repeatedly.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);
        var request = await InsertRequestAsync(account, exposure, _brokerId);

        await InsertJobAsync(account, request, attempt: 1);
        await InsertJobAsync(account, request, attempt: 2);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertJobAsync(account, request, attempt: 2));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_job_cannot_be_hung_off_another_accounts_request()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var theirExposure = await SeedExposureAsync(theirs, _brokerId);
        var theirRequest = await InsertRequestAsync(theirs, theirExposure, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertJobAsync(mine, theirRequest, attempt: 1));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Theory]
    [InlineData("Removal request for Jane Doe")]
    [InlineData("connector with spaces")]
    [InlineData("UPPERCASE")]
    public async Task A_connector_id_that_is_not_an_identifier_is_refused(string connectorId)
    {
        // The column is free text because the set of connectors is a build-time fact. The
        // shape constraint is what stops it becoming somewhere a sentence — and a sentence
        // about a removal is a sentence with somebody's name in it.
        var account = await OpenAccountAsync();
        var exposure = await SeedExposureAsync(account, _brokerId);
        var request = await InsertRequestAsync(account, exposure, _brokerId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertJobAsync(account, request, attempt: 1, connectorId: connectorId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_demand_needs_no_listing_at_all()
    {
        // The point of the change. Nothing about the right to tell a broker to delete what
        // it holds, or to stop selling it, depends on having found a page with somebody's
        // name on it first — and a scan only ever sees what is publicly searchable.
        var account = await OpenAccountAsync();

        await InsertRequestAsync(account, exposureId: null, _brokerId);

        var citations = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.removal_request WHERE tenant_id = '{account.TenantId}' "
            + "AND exposure_id IS NULL");

        Assert.Equal(1L, citations);
    }

    [Fact]
    public async Task A_demand_cannot_be_made_for_another_accounts_identity()
    {
        // The subject of a demand is now a column of its own, so it needs the same
        // pinning every other cross-reference here has.
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(mine, exposureId: null, _brokerId, profileId: theirs.ProfileId));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_demand_cannot_cite_a_listing_found_for_a_different_identity()
    {
        // Both identities belong to one account, so the tenant key does not notice. What
        // it would mean is a form filled with one person's details, submitted citing a
        // listing found for somebody else — a household managing a dependent's profile is
        // exactly where this arises.
        var account = await OpenAccountAsync();
        var dependent = await AddDependentAsync(account);

        var theirListing = await SeedExposureAsync(
            account, _brokerId, profileId: dependent.ProfileId, scanId: dependent.ScanId);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, theirListing, _brokerId, profileId: account.ProfileId));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_demand_citing_the_matching_identity_is_accepted()
    {
        // Guards the guard: a key that refused everything would pass the test above while
        // breaking the ordinary case.
        var account = await OpenAccountAsync();
        var dependent = await AddDependentAsync(account);

        var theirListing = await SeedExposureAsync(
            account, _brokerId, profileId: dependent.ProfileId, scanId: dependent.ScanId);

        await InsertRequestAsync(account, theirListing, _brokerId, profileId: dependent.ProfileId);

        Assert.Equal(1L, await RequestCountAsync(account.TenantId));
    }

    [Fact]
    public async Task A_listing_cannot_claim_an_identity_its_scan_was_not_searching_for()
    {
        // What holds up the test above. Pinning a demand to its listing's identity buys
        // nothing if the listing's identity can itself disagree with the run that found
        // it — the two keys are one guarantee in two halves.
        var account = await OpenAccountAsync();
        var dependent = await AddDependentAsync(account);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            SeedExposureAsync(account, _brokerId, profileId: dependent.ProfileId));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task The_request_records_which_right_is_being_exercised()
    {
        // Not cosmetic. §11.2 picks the governing regime by intersecting residency, the
        // broker's confirmed statutes and the kind of demand, and deletion and opt-out
        // carry different deadlines under the same statute — so a stored deadline that did
        // not say which demand it was computed for could not be read back against it.
        var account = await OpenAccountAsync();

        await InsertRequestAsync(
            account, exposureId: null, _brokerId, requestType: "opt_out_targeted_ads");

        var recorded = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT request_type FROM public.removal_request WHERE tenant_id = '{account.TenantId}'");

        Assert.Equal("opt_out_targeted_ads", recorded);
    }

    [Fact]
    public async Task A_request_type_outside_the_three_rights_is_refused()
    {
        var account = await OpenAccountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRequestAsync(account, exposureId: null, _brokerId, requestType: "stop_it"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    /// <summary>
    /// A second identity on the same account, as §16.2 allows, with a scan of its own.
    /// </summary>
    /// <remarks>
    /// The scan is not incidental. An exposure's profile is pinned to the profile its scan
    /// searched for, so a listing for this identity has to come from a run for this
    /// identity — seeding one against the account's own scan is now refused, which is the
    /// constraint doing its job on the fixture before it does it on the code.
    /// </remarks>
    private async Task<(Guid ProfileId, Guid ScanId)> AddDependentAsync(Account account)
    {
        var profileId = Guid.NewGuid();
        var scanId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.privacy_profile
                 (id, tenant_id, relationship_type, attestation_version)
                 VALUES ('{profileId}', '{account.TenantId}', 'dependent', '2026-06-01');

             INSERT INTO public.scan
                 (id, tenant_id, privacy_profile_id, trigger, status)
                 VALUES ('{scanId}', '{account.TenantId}', '{profileId}', 'manual', 'queued');
             """);

        return (profileId, scanId);
    }

    private async Task<long> RequestCountAsync(Guid tenantId) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.removal_request WHERE tenant_id = '{tenantId}'");

    private async Task<Guid> InsertRequestAsync(
        Account account,
        Guid? exposureId,
        Guid brokerId,
        string status = "queued",
        string deadlineSource = "operational_default",
        Guid? basisId = null,
        Guid? profileId = null,
        string requestType = "delete")
    {
        var id = Guid.NewGuid();
        var basis = basisId is { } value ? $"'{value}'" : "NULL";
        var exposure = exposureId is { } found ? $"'{found}'" : "NULL";

        await postgres.ExecuteAsOwnerAsync(
            $"""
             SET ROLE dbr_app;
             SELECT set_config('app.tenant_id', '{account.TenantId}', false);
             INSERT INTO public.removal_request
                 (id, tenant_id, privacy_profile_id, exposure_id, broker_id, request_type,
                  status, strategy, attempt, legal_basis_id, deadline_source, deadline_at)
                 VALUES ('{id}', '{account.TenantId}', '{profileId ?? account.ProfileId}',
                         {exposure}, '{brokerId}', '{requestType}', '{status}',
                         'automated', 0, {basis}, '{deadlineSource}', now() + interval '45 days');
             """);

        return id;
    }

    private async Task InsertJobAsync(
        Account account,
        Guid requestId,
        int attempt,
        string connectorId = "generic-web-form")
    {
        await postgres.ExecuteAsOwnerAsync(
            $"""
             SET ROLE dbr_app;
             SELECT set_config('app.tenant_id', '{account.TenantId}', false);
             INSERT INTO public.removal_job
                 (tenant_id, removal_request_id, connector_id, status, attempt_number, run_at)
                 VALUES ('{account.TenantId}', '{requestId}', '{connectorId}', 'pending',
                         {attempt}, now());
             """);
    }

    private async Task<Guid> SeedExposureAsync(
        Account account,
        Guid brokerId,
        Guid? profileId = null,
        Guid? scanId = null)
    {
        var id = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.exposure
                 (id, tenant_id, scan_id, privacy_profile_id, broker_id, status, confidence)
                 VALUES ('{id}', '{account.TenantId}', '{scanId ?? account.ScanId}',
                         '{profileId ?? account.ProfileId}', '{brokerId}', 'new', 0.9);
             """);

        return id;
    }

    private async Task<Account> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"rem-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        var (_, scan) = await _api.PostAsync("/api/v1/scans", new { }, token);

        return new Account(
            ApiClient.TenantId(session),
            scan.GetProperty("id").GetGuid(),
            scan.GetProperty("profileId").GetGuid());
    }

    private sealed record Account(Guid TenantId, Guid ScanId, Guid ProfileId);
}
