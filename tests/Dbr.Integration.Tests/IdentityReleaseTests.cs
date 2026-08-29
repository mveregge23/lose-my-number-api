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
/// Minting the permission to see part of an identity, and spending it.
/// </summary>
/// <remarks>
/// <para>
/// The claims worth testing here are the ones a unit test cannot make. That a grant opens
/// only the groups it named is a statement about which ciphertexts were passed to a real
/// cipher under a key a real key manager unwrapped. That a grant cannot be widened after
/// minting is a column privilege, enforced by Postgres and invisible to any code path.
/// And that spending is single-use is a race, which only means something against a
/// database that can actually run two statements.
/// </para>
/// <para>
/// Nothing here goes over HTTP, because there is no route yet — the listener a worker
/// would call is its own story. What that changes is only how the service is reached; the
/// service, the database, the vault and the key manager are all the real ones.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class IdentityReleaseTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ProfilePath = "/api/v1/profile";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _brokerId;

    private Guid _otherBrokerId;

    private string BrokerDomain => $"release-broker-{_suffix}.test";

    private string OtherBrokerDomain => $"release-other-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Release Broker {_suffix}', '{BrokerDomain}', 'webform', 45, true),
                        ('Release Other {_suffix}', '{OtherBrokerDomain}', 'email', 30, true);
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

        await postgres.ExecuteAsOwnerAsync(
            $"""
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
    /// The property the whole design is for.
    /// </summary>
    /// <remarks>
    /// The profile has a name, an address, a contact and a date of birth. The grant names
    /// one group, and what comes back holds one group — not because the others were
    /// decrypted and dropped, but because their bytes were never handed to the cipher.
    /// A test asserting only that names are present would pass just as happily against a
    /// service that decrypted everything.
    /// </remarks>
    [Fact]
    public async Task A_grant_opens_only_the_group_it_named()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);
        var granted = await RedeemAsync(minted.Token);

        Assert.Equal(RedeemReleaseOutcome.Granted, granted.Outcome);

        var identity = granted.Release!.Identity;

        Assert.Equal(["Alex Whitfield"], identity.Names);
        Assert.Empty(identity.Addresses);
        Assert.Empty(identity.Contacts);
        Assert.Null(identity.DateOfBirth);
    }

    [Fact]
    public async Task A_grant_naming_more_than_one_group_opens_all_of_them()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(
            account.TenantId,
            scanId,
            _brokerId,
            IdentityField.Names,
            IdentityField.Addresses);

        var granted = await RedeemAsync(minted.Token);

        Assert.Equal(RedeemReleaseOutcome.Granted, granted.Outcome);
        Assert.Equal(["Alex Whitfield"], granted.Release!.Identity.Names);
        Assert.Single(granted.Release.Identity.Addresses);

        // Still not the ones nobody asked for.
        Assert.Empty(granted.Release.Identity.Contacts);
        Assert.Null(granted.Release.Identity.DateOfBirth);
    }

    /// <summary>
    /// Single-use, and the claim is what enforces it rather than the read before it.
    /// </summary>
    [Fact]
    public async Task A_grant_can_be_spent_once()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        Assert.Equal(RedeemReleaseOutcome.Granted, (await RedeemAsync(minted.Token)).Outcome);
        Assert.Equal(RedeemReleaseOutcome.Refused, (await RedeemAsync(minted.Token)).Outcome);
    }

    /// <summary>
    /// Callers arriving together, and one identity between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sequential test above passes even if the claim does not check that the grant
    /// is unspent, because the read before it already found a spent one. That read is a
    /// courtesy — it saves a pointless write on the ordinary case — and it is not what
    /// makes single-use true. When two callers arrive together both reads see an unspent
    /// grant, and the only thing between them and two decryptions is that the update
    /// names the condition it depends on.
    /// </para>
    /// <para>
    /// Found by mutation rather than by design: deleting <c>RedeemedAt == null</c> from
    /// the claim broke no test at all, which meant the property everyone would assume was
    /// covered was not. Several racers rather than two, so they cannot all queue behind a
    /// first one that has already finished — which would let the read decide it again and
    /// leave the claim untested for the same reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Callers_racing_for_one_token_do_not_both_get_an_identity()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        var racers = Enumerable.Range(0, 8)
            .Select(_ => RedeemAsync(minted.Token))
            .ToArray();

        var results = await Task.WhenAll(racers);

        Assert.Single(results, result => result.Outcome == RedeemReleaseOutcome.Granted);
    }

    [Fact]
    public async Task Spending_a_grant_records_when_it_happened()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        Assert.False(await HasBeenSpentAsync(minted.Id));

        await RedeemAsync(minted.Token);

        // The record of a decryption, which is what the audit trail will carry when there
        // is one and what stands in for it until then.
        Assert.True(await HasBeenSpentAsync(minted.Id));
    }

    [Fact]
    public async Task A_grant_whose_window_has_passed_is_refused()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        // Both timestamps move, because a grant that expired before it was issued is a
        // state the table refuses outright — which is how this test first found out that
        // the constraint works. What it wants is an ordinary grant whose window has since
        // closed.
        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.identity_release "
            + $"SET issued_at = now() - interval '2 hours', "
            + $"    expires_at = now() - interval '1 hour' "
            + $"WHERE id = '{minted.Id}'");

        Assert.Equal(RedeemReleaseOutcome.Refused, (await RedeemAsync(minted.Token)).Outcome);
    }

    [Fact]
    public async Task A_token_nobody_minted_opens_nothing()
    {
        await OpenScanningAccountAsync();

        Assert.Equal(
            RedeemReleaseOutcome.Refused,
            (await RedeemAsync("not-a-token-anybody-issued")).Outcome);
    }

    /// <summary>
    /// The identity comes from the run, not from the caller.
    /// </summary>
    /// <remarks>
    /// There is no profile id in the signature, which is the point: a second place to name
    /// an identity is a second place to name the wrong one, and the scan already settled
    /// which one is being searched for.
    /// </remarks>
    [Fact]
    public async Task A_grant_names_the_identity_its_scan_named()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        var onGrant = await IdOfAsync(
            $"SELECT privacy_profile_id FROM public.identity_release WHERE id = '{minted.Id}'");

        var onScan = await IdOfAsync(
            $"SELECT privacy_profile_id FROM public.scan WHERE id = '{scanId}'");

        Assert.Equal(onScan, onGrant);
    }

    [Fact]
    public async Task A_run_that_is_over_cannot_mint_a_fresh_decryption_right()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        // The timestamps come off the row rather than from now(). The application writes
        // requested_at from its own clock and this statement would take Postgres's, and
        // the two are not the same clock — the database runs in a VM whose time can sit
        // marginally behind the host's, which makes started_at land before the request
        // that caused it and trips scan_timestamps_ordered. Deriving from the row is both
        // deterministic and a truer description of the state being set up.
        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.scan "
            + $"SET status = 'completed', started_at = requested_at, completed_at = requested_at "
            + $"WHERE id = '{scanId}'");

        var result = await TryMintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.ScanNotRunnable, result.Outcome);
    }

    /// <summary>
    /// A narrowed scan is a statement about who gets asked.
    /// </summary>
    [Fact]
    public async Task A_broker_the_scan_was_narrowed_away_from_cannot_mint()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token, _brokerId);

        var result = await TryMintAsync(account.TenantId, scanId, _otherBrokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.BrokerNotInScan, result.Outcome);
    }

    [Fact]
    public async Task A_broker_the_catalog_does_not_have_cannot_mint()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var result = await TryMintAsync(account.TenantId, scanId, Guid.NewGuid(), IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.UnknownBroker, result.Outcome);
    }

    [Fact]
    public async Task A_grant_covering_nothing_is_refused()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);

        var result = await TryMintAsync(account.TenantId, scanId, _brokerId);

        Assert.Equal(MintReleaseOutcome.NothingRequested, result.Outcome);
    }

    /// <summary>
    /// Somebody else's run is not a run, which is the same answer everywhere else.
    /// </summary>
    [Fact]
    public async Task An_account_cannot_mint_against_another_accounts_scan()
    {
        var owner = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(owner.Token);

        var stranger = await OpenScanningAccountAsync();

        var result = await TryMintAsync(stranger.TenantId, scanId, _brokerId, IdentityField.Names);

        Assert.Equal(MintReleaseOutcome.ScanNotFound, result.Outcome);
    }

    /// <summary>
    /// What the application role may change about a grant, and what it may not.
    /// </summary>
    /// <remarks>
    /// The two most useful edits to somebody holding an application-level foothold are
    /// widening the scope and pushing out the expiry, and neither is a privilege the role
    /// has. This is a column grant rather than a rule in code, so no code path can be
    /// written that gets round it.
    /// </remarks>
    [Fact]
    public async Task The_application_role_cannot_widen_a_grant_or_extend_it()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);
        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        var widen = await Assert.ThrowsAsync<PostgresException>(() =>
            AsApplicationAsync(
                account.TenantId,
                $"UPDATE public.identity_release SET fields = ARRAY['names','date_of_birth'] "
                + $"WHERE id = '{minted.Id}'"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, widen.SqlState);

        var extend = await Assert.ThrowsAsync<PostgresException>(() =>
            AsApplicationAsync(
                account.TenantId,
                $"UPDATE public.identity_release SET expires_at = now() + interval '1 day' "
                + $"WHERE id = '{minted.Id}'"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, extend.SqlState);
    }

    [Fact]
    public async Task The_application_role_may_record_that_a_grant_was_spent()
    {
        // The counterpart of the test above, and what makes it meaningful: the privilege
        // is narrowed to one column rather than revoked, so the refusals are about the
        // columns and not about the table.
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);
        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        // Off the row for the same reason the scan's timestamps are: issued_at came from
        // the application's clock, and identity_release_redeemed_after_issue compares
        // against it.
        await AsApplicationAsync(
            account.TenantId,
            $"UPDATE public.identity_release SET redeemed_at = issued_at WHERE id = '{minted.Id}'");

        Assert.True(await HasBeenSpentAsync(minted.Id));
    }

    [Fact]
    public async Task The_token_itself_is_never_stored()
    {
        var account = await OpenScanningAccountAsync();
        var scanId = await QueueScanAsync(account.Token);
        var minted = await MintAsync(account.TenantId, scanId, _brokerId, IdentityField.Names);

        // Nothing in the row is the token. What is stored is a digest, so reading this
        // table yields nothing anybody can present.
        var matches = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.identity_release "
            + $"WHERE encode(token_hash, 'escape') = '{minted.Token}'");

        Assert.Equal(0L, matches);
    }

    [Fact]
    public async Task Only_the_application_role_may_resolve_a_token()
    {
        Assert.True(await postgres.QueryAsOwnerAsync<bool>(
            "SELECT has_function_privilege('dbr_app', 'app.find_identity_release(bytea)', 'execute')"));

        Assert.False(await postgres.QueryAsOwnerAsync<bool>(
            "SELECT has_function_privilege('public', 'app.find_identity_release(bytea)', 'execute')"));
    }

    private async Task<MintedRelease> MintAsync(
        Guid tenantId,
        Guid scanId,
        Guid brokerId,
        params IdentityField[] fields)
    {
        var result = await TryMintAsync(tenantId, scanId, brokerId, fields);

        Assert.Equal(MintReleaseOutcome.Minted, result.Outcome);

        return result.Release!;
    }

    private async Task<MintReleaseResult> TryMintAsync(
        Guid tenantId,
        Guid scanId,
        Guid brokerId,
        params IdentityField[] fields)
    {
        // The API's own container, so this asks the service the composition root builds
        // rather than a second one wired up to match.
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseService>()
            .MintAsync(scanId, brokerId, fields, TestContext.Current.CancellationToken);
    }

    private async Task<RedeemReleaseResult> RedeemAsync(string token)
    {
        // A fresh scope acting for nobody, which is the situation the redeemer is
        // actually in: it holds a token and no session, and the tenant it ends up acting
        // for is the one the grant resolved to.
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseService>()
            .RedeemAsync(token, TestContext.Current.CancellationToken);
    }

    private async Task<(string Token, Guid TenantId)> OpenScanningAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"release-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);
        var tenantId = ApiClient.TenantId(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        // An identity with something in every group, so a scoped release has something to
        // leave behind.
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

    /// <summary>
    /// A statement run the way the application runs one: as the restricted role, with a
    /// tenant established, and therefore under both the policies and the column grants.
    /// </summary>
    private Task AsApplicationAsync(Guid tenantId, string sql) =>
        postgres.ExecuteAsOwnerAsync(
            $"""
             SET ROLE dbr_app;
             SELECT set_config('app.tenant_id', '{tenantId}', false);
             {sql};
             """);

    /// <summary>
    /// Whether the grant carries a spent timestamp.
    /// </summary>
    /// <remarks>
    /// Asked as a boolean rather than read as a timestamp, because the column is
    /// <c>timestamptz</c> and the fixture hands back whatever Npgsql maps it to. What the
    /// tests care about is that something was written there, and the database can answer
    /// that without either side agreeing on a CLR type.
    /// </remarks>
    private async Task<bool> HasBeenSpentAsync(Guid releaseId) =>
        await postgres.QueryAsOwnerAsync<bool>(
            $"SELECT redeemed_at IS NOT NULL FROM public.identity_release WHERE id = '{releaseId}'");

    private async Task<Guid> IdOfAsync(string sql) => await postgres.QueryAsOwnerAsync<Guid>(sql);
}
