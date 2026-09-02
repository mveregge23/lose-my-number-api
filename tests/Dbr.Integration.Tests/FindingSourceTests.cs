// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Tenancy;
using Dbr.Infrastructure.Vault;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Where a finding was found, and which side of the boundary it lands on.
/// </summary>
/// <remarks>
/// <para>
/// A broker's profile URL routinely spells out the name and the city of the person it is
/// about, so it is a copy of an identity rather than a pointer to one. The claims worth
/// testing are the ones a unit test cannot make: that the address really is absent from the
/// core store, that the bytes in the vault decrypt only in the position they were written to,
/// and that reporting is single-use against a database that can actually run two statements.
/// </para>
/// <para>
/// Nothing here goes over HTTP. The route a worker calls exists and has its own tests; this is
/// about the grant, what it writes, and where.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class FindingSourceTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ProfilePath = "/api/v1/profile";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private static readonly Uri Listing =
        new("https://example-broker.test/profile/alex-whitfield-sacramento-ca-41");

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _brokerId;

    private string BrokerDomain => $"finding-broker-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Finding Broker {_suffix}', '{BrokerDomain}', 'webform', 45, true);
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");
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
    /// The property the whole arrangement is for.
    /// </summary>
    /// <remarks>
    /// The URL names the person: it has their name, their city and their age in it. After a
    /// finding is recorded, the core store holds a digest and a confidence and no part of that
    /// sentence — and the vault holds the address, encrypted.
    /// </remarks>
    [Fact]
    public async Task The_address_of_a_listing_is_not_in_the_core_store()
    {
        var leg = await LegAsync();

        var result = await ReportAsync(leg.Token, Exactly(Listing));

        Assert.Equal(ReportFindingsOutcome.Recorded, result.Outcome);
        Assert.Equal(1, result.Recorded);

        // Nothing anywhere in the core row carries it. Asked of every text column at once
        // rather than of the one this story added, because the failure worth catching is a
        // later story putting it somewhere else.
        var anywhere = await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(*) FROM public.exposure WHERE exposure::text LIKE '%whitfield%'");

        Assert.Equal(0, anywhere);

        var inVault = await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(*) FROM vault.exposure_source "
            + "WHERE encode(encrypted_source_ref, 'escape') LIKE '%whitfield%'");

        Assert.Equal(0, inVault);
    }

    [Fact]
    public async Task The_address_comes_back_out_of_the_vault()
    {
        var leg = await LegAsync();

        await ReportAsync(leg.Token, Exactly(Listing));

        var exposureId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.exposure WHERE scan_id = '{leg.ScanId}'");

        Assert.Equal(Listing.AbsoluteUri, await ReadSourceAsync(leg.TenantId, exposureId));
    }

    /// <summary>
    /// The bytes decrypt in the position they were written to and nowhere else.
    /// </summary>
    /// <remarks>
    /// The same guarantee a profile field carries, which matters here for a specific reason:
    /// findings are the rows most likely to be moved about in bulk — purged, re-scanned,
    /// migrated — and a ciphertext that decrypted anywhere would let one account's listing
    /// surface under another's by way of a mistaken UPDATE.
    /// </remarks>
    [Fact]
    public async Task A_listing_address_does_not_decrypt_against_another_finding()
    {
        var leg = await LegAsync();

        await ReportAsync(leg.Token, Exactly(Listing));

        var exposureId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.exposure WHERE scan_id = '{leg.ScanId}'");

        // The tag mismatch specifically, which is the cipher saying the bytes were not written
        // for this position rather than the more general "something was wrong" a base
        // CryptographicException would allow.
        await Assert.ThrowsAsync<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => ReadSourceAsync(leg.TenantId, exposureId, readAs: Guid.NewGuid()));
    }

    /// <summary>Reporting is its own single-use spend.</summary>
    [Fact]
    public async Task A_leg_can_report_once()
    {
        var leg = await LegAsync();

        Assert.Equal(ReportFindingsOutcome.Recorded, (await ReportAsync(leg.Token, Exactly(Listing))).Outcome);
        Assert.Equal(ReportFindingsOutcome.Refused, (await ReportAsync(leg.Token, Exactly(Listing))).Outcome);

        Assert.Equal(
            1,
            await postgres.QueryAsOwnerAsync<long>(
                $"SELECT count(*) FROM public.exposure WHERE scan_id = '{leg.ScanId}'"));
    }

    /// <summary>
    /// Callers arriving together, and one leg's findings between them.
    /// </summary>
    /// <remarks>
    /// The sequential test above passes even if the claim does not check that reporting is
    /// unspent, because the read before it already found a spent grant. That read is a
    /// courtesy; when several callers arrive together they all see an unreported grant, and
    /// the only thing between them and several copies of everything is that the update names
    /// the condition it depends on. The same lesson the release path learned by mutation.
    /// </remarks>
    [Fact]
    public async Task Callers_arriving_together_do_not_all_record()
    {
        var leg = await LegAsync();

        // Held at a gate and released together, so their reads genuinely overlap. Started one
        // after another they tend to queue behind whichever finishes first, and each later one
        // then sees a grant already reported — which is the read deciding it, and the read is
        // exactly the thing this test must not be allowed to rely on.
        using var gate = new SemaphoreSlim(0);

        var racers = Enumerable.Range(0, 16)
            .Select(async _ =>
            {
                await gate.WaitAsync(TestContext.Current.CancellationToken);

                return await ReportAsync(leg.Token, Exactly(Listing));
            })
            .ToArray();

        gate.Release(racers.Length);

        var results = await Task.WhenAll(racers);

        Assert.Single(results, result => result.Outcome is ReportFindingsOutcome.Recorded);

        Assert.Equal(
            1,
            await postgres.QueryAsOwnerAsync<long>(
                $"SELECT count(*) FROM public.exposure WHERE scan_id = '{leg.ScanId}'"));
    }

    /// <summary>
    /// Opening the identity and saying what was found are separate permissions.
    /// </summary>
    [Fact]
    public async Task Redeeming_a_grant_does_not_consume_its_right_to_report()
    {
        var leg = await LegAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var redeemed = await scope.ServiceProvider
                .GetRequiredService<IIdentityReleaseRedeemer>()
                .RedeemAsync(leg.Token, TestContext.Current.CancellationToken);

            Assert.Equal(RedeemReleaseOutcome.Granted, redeemed.Outcome);
        }

        Assert.Equal(ReportFindingsOutcome.Recorded, (await ReportAsync(leg.Token, Exactly(Listing))).Outcome);
    }

    [Fact]
    public async Task Reporting_a_grant_does_not_consume_its_right_to_open_the_identity()
    {
        var leg = await LegAsync();

        Assert.Equal(ReportFindingsOutcome.Recorded, (await ReportAsync(leg.Token, Exactly(Listing))).Outcome);

        using var scope = _factory.Services.CreateScope();

        var redeemed = await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseRedeemer>()
            .RedeemAsync(leg.Token, TestContext.Current.CancellationToken);

        Assert.Equal(RedeemReleaseOutcome.Granted, redeemed.Outcome);
    }

    /// <summary>The floor is applied here, not by whatever reported.</summary>
    [Fact]
    public async Task A_listing_below_the_floor_is_counted_and_not_written()
    {
        var leg = await LegAsync();

        var result = await ReportAsync(
            leg.Token,
            [new ReportedListing(Listing, [new FieldMatch(IdentityField.Names, MatchStrength.Exact)])]);

        Assert.Equal(0, result.Recorded);
        Assert.Equal(1, result.BelowFloor);

        Assert.Equal(
            0,
            await postgres.QueryAsOwnerAsync<long>(
                $"SELECT count(*) FROM public.exposure WHERE scan_id = '{leg.ScanId}'"));

        // And nothing was written to the vault for a finding that does not exist.
        Assert.Equal(
            0,
            await postgres.QueryAsOwnerAsync<long>("SELECT count(*) FROM vault.exposure_source"));
    }

    /// <summary>One listing is one candidate, however many times it is reported.</summary>
    [Fact]
    public async Task One_listing_reported_twice_is_one_finding()
    {
        var leg = await LegAsync();

        var result = await ReportAsync(leg.Token, [.. Exactly(Listing), .. Exactly(Listing)]);

        Assert.Equal(1, result.Recorded);

        Assert.Equal(
            1,
            await postgres.QueryAsOwnerAsync<long>(
                $"SELECT count(*) FROM public.exposure WHERE scan_id = '{leg.ScanId}'"));
    }

    /// <summary>
    /// And the database says so as well, not only the code.
    /// </summary>
    /// <remarks>
    /// The in-memory check handles the honest case. This is the backstop for the rest — a
    /// retried leg, a migration, a hand-written row — and it is a constraint rather than a
    /// rule in code so that no path can be written that gets round it.
    /// </remarks>
    [Fact]
    public async Task The_database_refuses_one_listing_twice_on_one_run()
    {
        var leg = await LegAsync();

        await ReportAsync(leg.Token, Exactly(Listing));

        var digest = await postgres.QueryAsOwnerAsync<byte[]>(
            $"SELECT source_ref_digest FROM public.exposure WHERE scan_id = '{leg.ScanId}'");

        Assert.NotNull(digest);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 INSERT INTO public.exposure
                     (tenant_id, scan_id, privacy_profile_id, broker_id, status, confidence,
                      source_ref_digest)
                 SELECT tenant_id, scan_id, privacy_profile_id, broker_id, 'new', 0.9,
                        source_ref_digest
                 FROM public.exposure WHERE scan_id = '{leg.ScanId}';
                 """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_token_nobody_minted_records_nothing()
    {
        await LegAsync();

        Assert.Equal(
            ReportFindingsOutcome.Refused,
            (await ReportAsync("not-a-token-anybody-issued", Exactly(Listing))).Outcome);
    }

    // ---------------------------------------------------------------------------------

    private static IReadOnlyList<ReportedListing> Exactly(Uri source) =>
        [
            new ReportedListing(
                source,
                [
                    new FieldMatch(IdentityField.Names, MatchStrength.Exact),
                    new FieldMatch(IdentityField.Addresses, MatchStrength.Exact),
                ]),
        ];

    private async Task<ReportFindingsResult> ReportAsync(
        string token,
        IReadOnlyList<ReportedListing> listings)
    {
        // A fresh scope acting for nobody, which is the situation the reporter is actually
        // in: it holds a token and no session, and the tenant it ends up acting for is the
        // one the grant resolved to.
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IFindingReporter>()
            .ReportAsync(token, listings, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads a finding's address back, as the vault service would.</summary>
    private async Task<string> ReadSourceAsync(Guid tenantId, Guid exposureId, Guid? readAs = null)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        var vault = scope.ServiceProvider.GetRequiredService<VaultDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<IKeyManagementProvider>();

        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(
                vault.Set<ExposureSource>(),
                source => source.ExposureId == exposureId,
                TestContext.Current.CancellationToken);

        using var key = await keys.UnwrapDataKeyAsync(
            tenantId,
            row.WrappedDataKey,
            TestContext.Current.CancellationToken);

        return ExposureSourceCipher.Decrypt(
            key,
            new ExposureSourceBinding(tenantId, readAs ?? exposureId),
            row.EncryptedSourceRef);
    }

    /// <summary>An account, a run, and a minted grant for one company's leg of it.</summary>
    private async Task<(string Token, Guid TenantId, Guid ScanId)> LegAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"finding-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);
        var tenantId = ApiClient.TenantId(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        await _api.PutAsync(ProfilePath, new { names = new[] { "Alex Whitfield" } }, token);

        var (status, scan) = await _api.PostAsync(ScansPath, new { brokerIds = new[] { _brokerId } }, token);

        Assert.Equal(HttpStatusCode.Accepted, status);

        var scanId = scan.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        var minted = await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseMinter>()
            .MintAsync(
                scanId,
                _brokerId,
                [IdentityField.Names],
                TestContext.Current.CancellationToken);

        Assert.Equal(MintReleaseOutcome.Minted, minted.Outcome);

        return (minted.Release!.Token, tenantId, scanId);
    }
}
