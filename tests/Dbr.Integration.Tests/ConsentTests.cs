// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Domain.Consent;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// The three permissions: what a client is told, what gets written down, and what
/// cannot be written over afterwards.
/// </summary>
/// <remarks>
/// Over HTTP and against a real Postgres, because the two claims worth checking are both
/// properties of the database rather than of the code: that a revocation leaves the
/// grant it replaced in place, and that the application role has no way to rewrite
/// either. Neither survives a test against an in-memory provider with no roles and no
/// grants.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ConsentTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
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

        // Consent records first: they reference the tenant and deliberately do not
        // cascade, so the account cannot go until its decisions have.
        await postgres.ExecuteAsOwnerAsync(
            "DELETE FROM public.consent_record; DELETE FROM vault.profile_identity; "
            + "DELETE FROM public.privacy_profile; DELETE FROM public.tenant; "
            + "DELETE FROM public.passkey_ceremony;");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Every_consent_route_refuses_a_request_with_no_token(string method)
    {
        var (status, _) = method == "GET"
            ? await _api.GetAsync(ConsentPath, null)
            : await _api.PostAsync(ConsentPath, new { }, null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_new_account_permits_nothing_and_is_told_which_text_to_show()
    {
        var (token, _) = await OpenAccountAsync();

        var (status, body) = await _api.GetAsync(ConsentPath, token);

        Assert.Equal(HttpStatusCode.OK, status);

        // The version is in the response because the client has to display that text
        // and echo the version back to change anything.
        Assert.Equal(DbrApiFactory.ConsentPolicyVersion, body.GetProperty("policyVersion").GetString());

        var grants = body.GetProperty("grants").EnumerateArray().ToList();

        // All three, not just the decided ones. A switch with nothing to render is a
        // switch a client has to invent a position for.
        Assert.Equal(
            ["scan", "auto_removal", "auto_resubmit"],
            grants.Select(grant => grant.GetProperty("scope").GetString()));

        foreach (var grant in grants)
        {
            Assert.False(grant.GetProperty("granted").GetBoolean());

            // Never asked is not a decision, so there is no date and no version to
            // report against it.
            Assert.Equal(JsonValueKind.Null, grant.GetProperty("since").ValueKind);
            Assert.Equal(JsonValueKind.Null, grant.GetProperty("policyVersion").ValueKind);
        }
    }

    [Fact]
    public async Task Granting_one_permission_leaves_the_other_two_alone()
    {
        // The reason there are three of them. Somebody who wants to see where they are
        // listed without anything being sent in their name has to be able to say exactly
        // that, and a grant that quietly spread would make the distinction decorative.
        var (token, _) = await OpenAccountAsync();

        var (status, granted) = await GrantAsync(token, "scan", true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(granted.GetProperty("granted").GetBoolean());
        Assert.Equal(DbrApiFactory.ConsentPolicyVersion, granted.GetProperty("policyVersion").GetString());

        var grants = await ReadAsync(token);

        Assert.True(grants["scan"].GetProperty("granted").GetBoolean());
        Assert.False(grants["auto_removal"].GetProperty("granted").GetBoolean());
        Assert.False(grants["auto_resubmit"].GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task Withdrawing_a_permission_leaves_the_grant_it_replaced_on_the_record()
    {
        // The whole reason this is a history rather than a switch. Months later the
        // question is not whether a scan may run now but whether it was permitted when
        // it ran, and an update in place answers that with a row saying it never was.
        var (token, tenantId) = await OpenAccountAsync();

        await GrantAsync(token, "scan", true);
        await GrantAsync(token, "scan", false);

        var grants = await ReadAsync(token);
        Assert.False(grants["scan"].GetProperty("granted").GetBoolean());

        var rows = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.consent_record WHERE tenant_id = '{tenantId}' AND scope = 'scan'");

        Assert.Equal(2L, rows);

        var everGranted = await postgres.QueryAsOwnerAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM public.consent_record WHERE tenant_id = '{tenantId}' "
            + "AND scope = 'scan' AND granted)");

        Assert.True(everGranted);
    }

    [Fact]
    public async Task The_application_role_cannot_rewrite_a_decision()
    {
        // The guarantee above is only as good as the grant behind it. Without this, a
        // future UPDATE somewhere would compile, run, and silently turn the history into
        // a switchboard.
        var (token, tenantId) = await OpenAccountAsync();

        await GrantAsync(token, "scan", true);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_app;
                 SELECT set_config('app.tenant_id', '{tenantId}', false);
                 UPDATE public.consent_record SET granted = false WHERE tenant_id = '{tenantId}';
                 """));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task Deciding_the_same_thing_twice_writes_it_down_once()
    {
        // A retry, a double-tapped switch, a form saved unchanged. The history is a list
        // of decisions somebody actually made, and rows that changed nothing would bury
        // the ones that did.
        var (token, tenantId) = await OpenAccountAsync();

        var (first, _) = await GrantAsync(token, "auto_removal", true);
        var (second, grant) = await GrantAsync(token, "auto_removal", true);

        // The client asked for a state and got it both times; which of the two wrote a
        // row is a fact about the history, not about the answer.
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.True(grant.GetProperty("granted").GetBoolean());

        var rows = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.consent_record WHERE tenant_id = '{tenantId}'");

        Assert.Equal(1L, rows);
    }

    [Fact]
    public async Task A_decision_made_against_replaced_wording_is_refused_rather_than_recorded()
    {
        // The same stance signup takes on the terms: a field holding what a client
        // claimed answers nothing later. What is worth keeping is a record of what
        // somebody was actually shown.
        var (token, tenantId) = await OpenAccountAsync();

        var (status, problem) = await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = "2019-01-01" },
            token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("replaced", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        var rows = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.consent_record WHERE tenant_id = '{tenantId}'");

        Assert.Equal(0L, rows);
    }

    [Theory]
    [InlineData("scanning", true)]
    [InlineData("scan", null)]
    public async Task A_request_that_names_no_decision_is_refused_with_a_message(string scope, bool? granted)
    {
        var (token, _) = await OpenAccountAsync();

        var (status, _) = await _api.PostAsync(
            ConsentPath,
            new { scope, granted, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task One_accounts_decisions_are_invisible_to_another()
    {
        var (mine, _) = await OpenAccountAsync();
        var (theirs, _) = await OpenAccountAsync();

        await GrantAsync(mine, "scan", true);

        var grants = await ReadAsync(theirs);

        // Not an error — the other account has three permissions of its own, all
        // undecided. What this catches is a route that resolved consent from anything
        // but the token.
        Assert.False(grants["scan"].GetProperty("granted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, grants["scan"].GetProperty("since").ValueKind);
    }

    [Fact]
    public async Task What_a_dispatcher_asks_is_the_same_answer_the_client_sees()
    {
        // The check a scan will run before it dispatches, against the same service the
        // settings screen reads. Two readings of "granted" would make the switch
        // decorative in exactly the way that is hardest to notice.
        var (token, tenantId) = await OpenAccountAsync();

        Assert.False(await IsGrantedAsync(tenantId, ConsentScope.Scan));

        await GrantAsync(token, "scan", true);
        Assert.True(await IsGrantedAsync(tenantId, ConsentScope.Scan));

        // The other two are not carried along by it.
        Assert.False(await IsGrantedAsync(tenantId, ConsentScope.AutoRemoval));

        await GrantAsync(token, "scan", false);
        Assert.False(await IsGrantedAsync(tenantId, ConsentScope.Scan));
    }

    private async Task<bool> IsGrantedAsync(Guid tenantId, ConsentScope scope)
    {
        // The API's own container, so this asks the service the endpoints resolve rather
        // than a second one wired up to match.
        using var scoped = _factory.Services.CreateScope();
        scoped.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        return await scoped.ServiceProvider
            .GetRequiredService<IConsentService>()
            .IsGrantedAsync(scope, TestContext.Current.CancellationToken);
    }

    private async Task<(HttpStatusCode Status, JsonElement Grant)> GrantAsync(
        string token,
        string scope,
        bool granted) =>
        await _api.PostAsync(
            ConsentPath,
            new { scope, granted, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

    private async Task<Dictionary<string, JsonElement>> ReadAsync(string token)
    {
        var (_, body) = await _api.GetAsync(ConsentPath, token);

        return body.GetProperty("grants")
            .EnumerateArray()
            .ToDictionary(grant => grant.GetProperty("scope").GetString()!);
    }

    private async Task<(string AccessToken, Guid TenantId)> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"consent-{Guid.NewGuid():N}@example.test", authenticator);

        return (ApiClient.AccessToken(session), ApiClient.TenantId(session));
    }
}
