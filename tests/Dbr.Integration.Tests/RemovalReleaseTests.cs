// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Minting the permission to make one demand, and what the schema will not let it be.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of the scan side's release tests, and the claims are the ones only a real
/// database can carry: that a grant names exactly one piece of work, that it cannot be
/// minted for an attempt that has already run or for a company the demand is not addressed
/// to, and that the two spends a token has are not interchangeable.
/// </para>
/// <para>
/// Nothing here goes over the internal edge. What these are about is the grant — what it
/// covers, what it refuses, what the database will not let anybody write — and a transport
/// in the middle would only add ways for them to fail for unrelated reasons.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class RemovalReleaseTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ProfilePath = "/api/v1/profile";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private const string RemovalsPath = "/api/v1/removal-requests";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _brokerId;

    private Guid _otherBrokerId;

    private string BrokerDomain => $"rel-job-{_suffix}.test";

    private string OtherBrokerDomain => $"rel-other-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Rel Job {_suffix}', '{BrokerDomain}', 'email', 30, true),
                        ('Rel Other {_suffix}', '{OtherBrokerDomain}', 'email', 30, true);
             """);

        _brokerId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");
        _otherBrokerId = await IdOfAsync(
            $"SELECT id FROM public.broker WHERE domain = '{OtherBrokerDomain}'");
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
             DELETE FROM public.identity_release;
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
    /// A grant for an attempt names the attempt and no run.
    /// </summary>
    /// <remarks>
    /// The whole widening in one assertion. Before this, every grant carried a scan id
    /// because the column could hold nothing else.
    /// </remarks>
    [Fact]
    public async Task A_grant_for_an_attempt_names_the_attempt_and_not_a_run()
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);

        var minted = await MintAsync(account.TenantId, jobId, _brokerId, IdentityField.Names);

        Assert.NotNull(minted.Release);

        var redeemed = await RedeemAsync(minted.Release.Token);

        Assert.Equal(RedeemReleaseOutcome.Granted, redeemed.Outcome);
        Assert.Equal(jobId, redeemed.Release!.RemovalJobId);
        Assert.Null(redeemed.Release.ScanId);
        Assert.Equal(_brokerId, redeemed.Release.BrokerId);
        Assert.Equal(["Alex Whitfield"], redeemed.Release.Identity.Names);
    }

    /// <summary>
    /// It opens the groups it named and leaves the rest in the vault.
    /// </summary>
    [Fact]
    public async Task A_grant_opens_only_what_it_covered()
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);

        var minted = await MintAsync(account.TenantId, jobId, _brokerId, IdentityField.Names);
        var redeemed = await RedeemAsync(minted.Release!.Token);

        var identity = redeemed.Release!.Identity;

        Assert.Equal(["Alex Whitfield"], identity.Names);
        Assert.Null(identity.DateOfBirth);
        Assert.Empty(identity.Contacts);
    }

    [Fact]
    public async Task An_attempt_that_does_not_exist_mints_nothing()
    {
        var account = await OpenAccountAsync();

        var minted = await MintAsync(account.TenantId, Guid.NewGuid(), _brokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.JobNotFound, minted.Outcome);
        Assert.Null(minted.Release);
    }

    /// <summary>
    /// Another account's attempt answers the same as one that does not exist.
    /// </summary>
    [Fact]
    public async Task Another_accounts_attempt_mints_nothing()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var theirJob = await AnAttemptAsync(theirs);

        var minted = await MintAsync(mine.TenantId, theirJob, _brokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.JobNotFound, minted.Outcome);
    }

    /// <summary>
    /// An attempt that has already run cannot mint a fresh decryption right.
    /// </summary>
    /// <remarks>
    /// The case that matters more here than on the scan side: work arriving late for a scan
    /// reads a page nobody sees, while this would open an identity in order to send a
    /// company a demand that has already gone or been withdrawn.
    /// </remarks>
    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task An_attempt_that_has_already_run_mints_nothing(string status)
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_job SET status = '{status}' WHERE id = '{jobId}'");

        var minted = await MintAsync(account.TenantId, jobId, _brokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.JobNotRunnable, minted.Outcome);
    }

    /// <summary>
    /// A grant cannot be minted for a company the demand is not addressed to.
    /// </summary>
    /// <remarks>
    /// There is no narrowing to check against the way a scan has — a demand names exactly
    /// one company — so this is the whole of the check, and skipping it would decrypt an
    /// identity for a company the person never asked to be contacted.
    /// </remarks>
    [Fact]
    public async Task A_company_the_demand_is_not_addressed_to_mints_nothing()
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);

        var minted = await MintAsync(account.TenantId, jobId, _otherBrokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.BrokerNotForThisJob, minted.Outcome);
    }

    [Fact]
    public async Task A_grant_covering_nothing_is_refused()
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);

        var minted = await MintAsync(account.TenantId, jobId, _brokerId);

        Assert.Equal(MintReleaseOutcome.NothingRequested, minted.Outcome);
    }

    [Fact]
    public async Task A_grant_for_an_attempt_is_single_use()
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);

        var minted = await MintAsync(account.TenantId, jobId, _brokerId, IdentityField.Names);

        Assert.Equal(RedeemReleaseOutcome.Granted, (await RedeemAsync(minted.Release!.Token)).Outcome);
        Assert.Equal(RedeemReleaseOutcome.Refused, (await RedeemAsync(minted.Release.Token)).Outcome);
    }

    /// <summary>
    /// A grant belongs to one piece of work, which the database holds rather than the code.
    /// </summary>
    /// <remarks>
    /// Written directly as the owning role, because the service has no way to express either
    /// of these — the two mint methods each set one column. What is being checked is that a
    /// row written any other way is refused: a grant naming neither would be a decryption
    /// right belonging to nothing, and one naming both would be two pieces of work sharing a
    /// single-use token.
    /// </remarks>
    [Fact]
    public async Task A_grant_naming_neither_piece_of_work_is_refused()
    {
        var account = await OpenAccountAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertGrantAsync(
            account,
            scanId: "NULL",
            removalJobId: "NULL"));

        Assert.Equal("identity_release_names_one_piece_of_work", error.ConstraintName);
    }

    [Fact]
    public async Task A_grant_naming_both_pieces_of_work_is_refused()
    {
        var account = await OpenAccountAsync();
        var jobId = await AnAttemptAsync(account);
        var scanId = await ScanOfAsync(account);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertGrantAsync(
            account,
            scanId: $"'{scanId}'",
            removalJobId: $"'{jobId}'"));

        Assert.Equal("identity_release_names_one_piece_of_work", error.ConstraintName);
    }

    /// <summary>
    /// A grant cannot be hung off another account's attempt.
    /// </summary>
    /// <remarks>
    /// Row-level security does not give this on its own — Postgres checks a foreign key with
    /// row security off — so it is the composite key that holds, and what it would mean is a
    /// grant opening one person's identity for another person's demand.
    /// </remarks>
    [Fact]
    public async Task A_grant_cannot_be_hung_off_another_accounts_attempt()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var theirJob = await AnAttemptAsync(theirs);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertGrantAsync(
            mine,
            scanId: "NULL",
            removalJobId: $"'{theirJob}'"));

        Assert.Equal("identity_release_job_same_tenant", error.ConstraintName);
    }

    private async Task InsertGrantAsync(Account account, string scanId, string removalJobId)
    {
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.identity_release
                 (tenant_id, scan_id, removal_job_id, broker_id, privacy_profile_id,
                  token_hash, fields, expires_at)
                 VALUES ('{account.TenantId}', {scanId}, {removalJobId}, '{_brokerId}',
                         '{account.ProfileId}', decode(md5(random()::text), 'hex'),
                         ARRAY['names']::text[], now() + interval '5 minutes');
             """);
    }

    /// <summary>One dispatched attempt, written the way the dispatcher writes one.</summary>
    private async Task<Guid> AnAttemptAsync(Account account)
    {
        var (status, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _brokerId, requestType = "delete" },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, status);

        var requestId = body.GetProperty("id").GetGuid();
        var jobId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             UPDATE public.removal_request SET status = 'submitted', attempt = 1
                 WHERE id = '{requestId}';

             INSERT INTO public.removal_job
                 (id, tenant_id, removal_request_id, connector_id, status, attempt_number, run_at)
                 VALUES ('{jobId}', '{account.TenantId}', '{requestId}', 'templated-email',
                         'pending', 1, now());
             """);

        return jobId;
    }

    private async Task<Guid> ScanOfAsync(Account account) =>
        await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.scan WHERE tenant_id = '{account.TenantId}' LIMIT 1");

    private async Task<MintReleaseResult> MintAsync(
        Guid tenantId,
        Guid jobId,
        Guid brokerId,
        params IdentityField[] fields)
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseMinter>()
            .MintForJobAsync(jobId, brokerId, fields, TestContext.Current.CancellationToken);
    }

    private async Task<RedeemReleaseResult> RedeemAsync(string token)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseRedeemer>()
            .RedeemAsync(token, TestContext.Current.CancellationToken);
    }

    private async Task<Account> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"rel-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        foreach (var scope in new[] { "scan", "auto_removal" })
        {
            await _api.PostAsync(
                ConsentPath,
                new { scope, granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
                token);
        }

        await _api.PutAsync(
            ProfilePath,
            new
            {
                names = new[] { "Alex Whitfield" },
                dateOfBirth = "1985-04-17",
                contacts = new[] { new { kind = "email", value = "alex@example.test" } },
            },
            token);

        var (_, scan) = await _api.PostAsync(ScansPath, new { }, token);

        return new Account(
            token,
            ApiClient.TenantId(session),
            scan.GetProperty("profileId").GetGuid());
    }

    private async Task<Guid> IdOfAsync(string sql) => await postgres.QueryAsOwnerAsync<Guid>(sql);

    private sealed record Account(string Token, Guid TenantId, Guid ProfileId);
}
