// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// The same guardrail from the two directions a unit test cannot reach: what the running
/// API does with an identity somebody sends it anyway, and what the schema will hold.
/// </summary>
/// <remarks>
/// The type-level assertions live in <c>Dbr.Api.Tests</c> and are the primary control.
/// These exist because a shape is only a guarantee where it is actually enforced: a
/// request field that is ignored rather than refused would let a client author believe
/// name-based search works, and a text column nobody constrained would let the identity
/// arrive by a different door entirely.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ScanGuardrailTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        return ValueTask.CompletedTask;
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
            """
            DELETE FROM public.exposure;
            DELETE FROM public.scan_broker;
            DELETE FROM public.scan;
            DELETE FROM public.consent_record;
            DELETE FROM vault.profile_identity;
            DELETE FROM public.privacy_profile;
            DELETE FROM public.tenant;
            DELETE FROM public.passkey_ceremony;
            """);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("fullName")]
    [InlineData("address")]
    [InlineData("dateOfBirth")]
    [InlineData("email")]
    [InlineData("query")]
    public async Task An_identity_sent_to_the_scan_route_is_refused_rather_than_ignored(string field)
    {
        // The failure this prevents is not a leak, it is a misunderstanding that becomes
        // one. Ignored, the field produces a perfectly good scan of the caller's own
        // profile, and whoever wrote the client concludes that name-based search works and
        // ships a search box. Refusing is what makes the absence of the feature legible.
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token);

        var body = new Dictionary<string, object?> { [field] = "Jane Doe" };

        var (status, _) = await _api.PostAsync(ScansPath, body, token);

        Assert.Equal(HttpStatusCode.BadRequest, status);

        // And nothing ran. A refused request that still queued a scan would be the worst
        // of both answers.
        var scans = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.scan WHERE tenant_id = '{tenantId}'");

        Assert.Equal(0L, scans);
    }

    [Fact]
    public async Task The_ordinary_request_still_works_alongside_that()
    {
        // Guards the guard: a refusal that rejected everything would pass the theory above
        // while breaking the endpoint.
        var (token, _) = await OpenAccountAsync();
        await GrantScanAsync(token);

        var (status, _) = await _api.PostAsync(ScansPath, new { }, token);

        Assert.Equal(HttpStatusCode.Accepted, status);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("scan_broker")]
    [InlineData("exposure")]
    public async Task No_table_in_the_scan_pipeline_has_an_unconstrained_text_column(string table)
    {
        // The other door. Every text column on these tables today holds one of a fixed set
        // of words and carries a check constraint saying so, which means none of them can
        // hold a name. A new text column without one would be the place an identity could
        // start being stored — and it would arrive looking like an ordinary migration.
        var unconstrained = await postgres.QueryAsOwnerAsync<string>(
            $"""
             SELECT string_agg(a.attname, ', ' ORDER BY a.attname)
             FROM pg_attribute a
             JOIN pg_class c ON c.oid = a.attrelid
             JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public'
               AND c.relname = '{table}'
               AND a.attnum > 0
               AND NOT a.attisdropped
               AND format_type(a.atttypid, a.atttypmod) IN ('text', 'character varying')
               AND NOT EXISTS (
                   SELECT 1 FROM pg_constraint con
                   WHERE con.conrelid = c.oid
                     AND con.contype = 'c'
                     AND a.attnum = ANY (con.conkey))
             """);

        Assert.True(
            unconstrained is null,
            $"public.{table} has free-text column(s) with no check constraint: {unconstrained}. A "
            + "text column on this pipeline holds one of a fixed set of words or it holds whatever "
            + "somebody put in it, and the second kind is where an identity would end up.");
    }

    private async Task GrantScanAsync(string token) =>
        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

    private async Task<(string AccessToken, Guid TenantId)> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"guard-{Guid.NewGuid():N}@example.test", authenticator);

        return (ApiClient.AccessToken(session), ApiClient.TenantId(session));
    }
}
