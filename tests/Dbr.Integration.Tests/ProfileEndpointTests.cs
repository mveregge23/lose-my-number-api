// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Domain.Profiles;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The profile routes as a client meets them: over HTTP, through the real pipeline, with
/// a real database and a real key manager behind them.
/// </summary>
/// <remarks>
/// The service underneath already has tests. What only shows up here is whether a route
/// requires a token, what a client is actually told when something is wrong, and whether
/// the round trip through JSON preserves what somebody typed — none of which the service
/// can answer about itself.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ProfileEndpointTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string Attestation = "2026-06-01";

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
            "DELETE FROM vault.profile_identity; DELETE FROM public.privacy_profile; "
            + "DELETE FROM public.tenant; DELETE FROM public.passkey_ceremony;");
    }

    [Theory]
    [InlineData("GET", "/api/v1/profile")]
    [InlineData("PUT", "/api/v1/profile")]
    [InlineData("POST", "/api/v1/profile/addresses")]
    [InlineData("DELETE", "/api/v1/profile/addresses/8a1d5f8e-1c2b-4a3d-9e6f-0b1c2d3e4f50")]
    public async Task Every_profile_route_refuses_a_request_with_no_token(string method, string path)
    {
        // All four, not one as a sample: authorization is declared on the group, and a
        // route mapped just outside it looks identical in review and is wide open.
        var (status, _) = method switch
        {
            "GET" => await _api.GetAsync(path, null),
            "PUT" => await _api.PutAsync(path, new { }, null),
            "POST" => await _api.PostAsync(path, new { }, null),
            _ => await _api.DeleteAsync(path, null),
        };

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task An_account_with_no_profile_is_told_so_rather_than_shown_an_empty_one()
    {
        // Reachable only until signup creates the profile itself. An empty profile here
        // would be indistinguishable from a real one somebody had not filled in, and the
        // difference decides whether a client offers to create or to edit.
        var session = await SignUpAsync();

        var (status, _) = await _api.GetAsync("/api/v1/profile", ApiClient.AccessToken(session));

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task A_profile_is_filled_in_and_reads_back_as_what_was_sent()
    {
        var (token, _) = await AccountWithProfileAsync();

        var (putStatus, _) = await _api.PutAsync(
            "/api/v1/profile",
            new
            {
                names = new[] { "Alex Whitfield", "A. Whitfield" },
                dateOfBirth = "1985-04-17",
                contacts = new[] { new { kind = "email", value = "alex@example.test" } },
                residencyRegion = "us-ca",
            },
            token);

        Assert.Equal(HttpStatusCode.NoContent, putStatus);

        var (status, profile) = await _api.GetAsync("/api/v1/profile", token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("self", profile.GetProperty("relationshipType").GetString());
        Assert.Equal(["Alex Whitfield", "A. Whitfield"], Strings(profile.GetProperty("names")));
        Assert.Equal("1985-04-17", profile.GetProperty("dateOfBirth").GetString());

        // Sent lower-case, stored upper-case: the catalog compares region codes, and two
        // spellings of California would not compare equal.
        Assert.Equal("US-CA", profile.GetProperty("residencyRegion").GetString());

        var contact = profile.GetProperty("contacts").EnumerateArray().Single();
        Assert.Equal("email", contact.GetProperty("kind").GetString());
        Assert.Equal("alex@example.test", contact.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Replacing_the_details_leaves_the_addresses_where_they_are()
    {
        // The reason addresses are a sub-resource at all. A client editing a phone
        // number sends no addresses, and an address somebody lived at years ago is
        // frequently the only reason a listing can be found — losing one to a partial
        // update would be silent and expensive.
        var (token, _) = await AccountWithProfileAsync();

        var (created, address) = await _api.PostAsync(
            "/api/v1/profile/addresses",
            new { line1 = "12 Rowan Lane", city = "Sacramento", region = "CA", postalCode = "95814", country = "us" },
            token);

        Assert.Equal(HttpStatusCode.Created, created);

        await _api.PutAsync(
            "/api/v1/profile",
            new { names = new[] { "Alex Whitfield" }, contacts = Array.Empty<object>() },
            token);

        var (_, profile) = await _api.GetAsync("/api/v1/profile", token);
        var stored = profile.GetProperty("addresses").EnumerateArray().Single();

        Assert.Equal(address.GetProperty("id").GetString(), stored.GetProperty("id").GetString());
        Assert.Equal("12 Rowan Lane", stored.GetProperty("line1").GetString());

        // Upper-cased on the way in, for the same reason the region code is.
        Assert.Equal("US", stored.GetProperty("country").GetString());
    }

    [Fact]
    public async Task An_address_is_added_and_removed_by_id()
    {
        var (token, _) = await AccountWithProfileAsync();

        var (_, address) = await _api.PostAsync(
            "/api/v1/profile/addresses",
            new { line1 = "12 Rowan Lane", city = "Sacramento", country = "US" },
            token);

        var id = address.GetProperty("id").GetString();

        var (removed, _) = await _api.DeleteAsync($"/api/v1/profile/addresses/{id}", token);
        Assert.Equal(HttpStatusCode.NoContent, removed);

        var (_, profile) = await _api.GetAsync("/api/v1/profile", token);
        Assert.Empty(profile.GetProperty("addresses").EnumerateArray());

        // Removing it again is a 404 rather than a second success, so a client can tell
        // "gone" from "never there" when it matters.
        var (again, _) = await _api.DeleteAsync($"/api/v1/profile/addresses/{id}", token);
        Assert.Equal(HttpStatusCode.NotFound, again);
    }

    [Fact]
    public async Task One_accounts_profile_is_invisible_to_another()
    {
        var (mine, _) = await AccountWithProfileAsync();
        var (theirs, _) = await AccountWithProfileAsync();

        await _api.PutAsync(
            "/api/v1/profile",
            new { names = new[] { "Alex Whitfield" } },
            mine);

        var (status, profile) = await _api.GetAsync("/api/v1/profile", theirs);

        // Not an error — the other account has a profile of its own, and it is empty.
        // The failure this catches is a route that resolved the profile from anything
        // but the token.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(profile.GetProperty("names").EnumerateArray());
    }

    [Fact]
    public async Task A_residency_region_that_is_really_an_address_is_refused_with_a_message()
    {
        // The database would refuse this too, as a check constraint — which reaches the
        // client as a 500 that says nothing. The point of validating it here is the
        // answer, not the protection.
        var (token, _) = await AccountWithProfileAsync();

        var (status, problem) = await _api.PutAsync(
            "/api/v1/profile",
            new { names = Array.Empty<string>(), residencyRegion = "12 Rowan Lane, Sacramento" },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("coarse code", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2999-01-01")]
    [InlineData("1800-01-01")]
    public async Task A_date_of_birth_that_cannot_be_one_is_refused(string dateOfBirth)
    {
        var (token, _) = await AccountWithProfileAsync();

        var (status, _) = await _api.PutAsync("/api/v1/profile", new { dateOfBirth }, token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task An_address_without_a_country_code_is_refused()
    {
        var (token, _) = await AccountWithProfileAsync();

        var (status, _) = await _api.PostAsync(
            "/api/v1/profile/addresses",
            new { line1 = "12 Rowan Lane", city = "Sacramento", country = "United States" },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_profile_stops_accepting_addresses_at_the_limit()
    {
        // Nothing in the database limits this: the addresses are one encrypted column,
        // and every edit rewrites all of them, so unbounded growth would make each
        // subsequent change more expensive with nothing to notice it.
        var (token, _) = await AccountWithProfileAsync();

        for (var i = 0; i < ProfileLimits.MaxAddresses; i++)
        {
            var (status, _) = await _api.PostAsync(
                "/api/v1/profile/addresses",
                new { line1 = $"{i} Rowan Lane", city = "Sacramento", country = "US" },
                token);

            Assert.Equal(HttpStatusCode.Created, status);
        }

        var (refused, _) = await _api.PostAsync(
            "/api/v1/profile/addresses",
            new { line1 = "one too many", city = "Sacramento", country = "US" },
            token);

        Assert.Equal(HttpStatusCode.Conflict, refused);
    }

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(value => value.GetString()!)];

    private async Task<JsonElement> SignUpAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        return await _api.SignUpAsync($"profile-{Guid.NewGuid():N}@example.test", authenticator);
    }

    /// <summary>
    /// An account whose self profile exists, created through the service the way signup
    /// is about to.
    /// </summary>
    /// <remarks>
    /// Signup does not create it yet — that is the next story — so these tests stand in
    /// for it rather than testing against a state no account will be in for long.
    /// </remarks>
    private async Task<(string AccessToken, Guid TenantId)> AccountWithProfileAsync()
    {
        var session = await SignUpAsync();
        var tenantId = ApiClient.TenantId(session);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        await scope.ServiceProvider.GetRequiredService<IProfileService>().CreateAsync(
            ProfileRelationship.Self,
            residencyRegion: null,
            Attestation,
            ProfileIdentityFields.Empty,
            Token);

        return (ApiClient.AccessToken(session), tenantId);
    }
}
