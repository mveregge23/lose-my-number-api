// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Opening an account, over the wire: what it creates besides the account, and what it
/// leaves behind when it cannot finish.
/// </summary>
/// <remarks>
/// The passkey ceremony itself is tested against the service directly, and this does not
/// repeat it. What only shows up here is the part that spans two stores and a key
/// manager — that a new account arrives with its own profile, that the profile records
/// what its owner actually accepted, and that a signup which cannot get that far leaves
/// nothing to be stuck with.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class SignupTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
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
            "DELETE FROM vault.profile_identity; DELETE FROM public.privacy_profile; "
            + "DELETE FROM public.tenant; DELETE FROM public.passkey_ceremony;");
    }

    [Fact]
    public async Task A_new_account_can_read_its_own_profile_without_creating_one()
    {
        var session = await _api.SignUpAsync(Unique(), NewAuthenticator());

        var (status, profile) = await _api.GetAsync("/api/v1/profile", ApiClient.AccessToken(session));

        // The whole point of the story: nothing between opening an account and having
        // somewhere to put the identity it exists to act on.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("self", profile.GetProperty("relationshipType").GetString());
        Assert.Empty(profile.GetProperty("names").EnumerateArray());
        Assert.Empty(profile.GetProperty("addresses").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("residencyRegion").ValueKind);
    }

    [Fact]
    public async Task The_profile_is_attested_by_the_terms_the_account_was_opened_under()
    {
        var session = await _api.SignUpAsync(Unique(), NewAuthenticator());

        var (_, profile) = await _api.GetAsync("/api/v1/profile", ApiClient.AccessToken(session));

        Assert.Equal(DbrApiFactory.TermsVersion, profile.GetProperty("attestationVersion").GetString());

        // Recorded at the moment of acceptance rather than left to a default, which is
        // the difference between an attestation and a column that is never null.
        Assert.InRange(
            profile.GetProperty("attestedAt").GetDateTimeOffset(),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task The_profile_is_the_one_the_account_already_manages()
    {
        // A second self profile is refused by the database, so this is really asking
        // whether signup created exactly one and whether the routes found it.
        var session = await _api.SignUpAsync(Unique(), NewAuthenticator());
        var tenantId = ApiClient.TenantId(session);

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.privacy_profile WHERE tenant_id = '{tenantId}'"));

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM vault.profile_identity WHERE tenant_id = '{tenantId}'"));
    }

    [Fact]
    public async Task Terms_that_are_no_longer_current_are_refused_and_open_no_account()
    {
        var email = Unique();
        var options = await _api.BeginSignUpAsync(email);

        var (status, _) = await _api.CompleteSignUpAsync(options, NewAuthenticator(), "1999-01-01");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(0, await AccountsForAsync(email));
    }

    [Fact]
    public async Task Accepting_nothing_at_all_is_refused_rather_than_recorded_as_agreement()
    {
        var email = Unique();
        var options = await _api.BeginSignUpAsync(email);

        var (status, _) = await _api.CompleteSignUpAsync(options, NewAuthenticator(), acceptedTermsVersion: null);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(0, await AccountsForAsync(email));
    }

    [Fact]
    public async Task A_refusal_over_stale_terms_leaves_the_registration_usable()
    {
        // The reason the version is checked before the ceremony is claimed. Somebody
        // whose terms moved while they were reading should be able to accept the current
        // ones without going back to their authenticator for a second challenge.
        var options = await _api.BeginSignUpAsync(Unique());

        var (refused, _) = await _api.CompleteSignUpAsync(options, NewAuthenticator(), "1999-01-01");
        Assert.Equal(HttpStatusCode.Conflict, refused);

        var (accepted, session) = await _api.CompleteSignUpAsync(
            options,
            NewAuthenticator(),
            options.GetProperty("termsVersion").GetString());

        Assert.Equal(HttpStatusCode.OK, accepted);

        var (status, _) = await _api.GetAsync("/api/v1/profile", ApiClient.AccessToken(session));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task The_challenge_carries_the_terms_the_client_has_to_show()
    {
        var options = await _api.BeginSignUpAsync(Unique());

        // Without this a client has nothing to display and nothing to echo, and the
        // acceptance it collects would be of text it chose for itself.
        Assert.Equal(DbrApiFactory.TermsVersion, options.GetProperty("termsVersion").GetString());
    }

    [Fact]
    public async Task An_account_whose_profile_cannot_be_created_is_not_left_behind()
    {
        // The failure this story could otherwise introduce: an account that exists,
        // cannot be signed up for again because the address is taken, and can never have
        // the profile every feature reads from. A key manager that is not answering is
        // the realistic way to get there.
        await using var broken = new DbrApiFactory(postgres.ConnectionString);
        using var client = broken.CreateClient();

        var api = new ApiClient(client);
        var email = Unique();

        var options = await api.BeginSignUpAsync(email);
        var (status, _) = await api.CompleteSignUpAsync(
            options,
            NewAuthenticator(),
            options.GetProperty("termsVersion").GetString());

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(0, await AccountsForAsync(email));

        // And the address is free, which is the part that matters to whoever was trying
        // to sign up: they can do it again once the instance is healthy.
        var session = await _api.SignUpAsync(email, NewAuthenticator());

        Assert.Equal(
            HttpStatusCode.OK,
            (await _api.GetAsync("/api/v1/profile", ApiClient.AccessToken(session))).Status);
    }

    private static string Unique() => $"signup-{Guid.NewGuid():N}@example.test";

    /// <summary>
    /// How many accounts exist for the address a test just tried to open one for.
    /// </summary>
    /// <remarks>
    /// Asked about this address rather than about the whole table. "No account was
    /// opened" is a statement about this signup, and counting every row makes it a
    /// statement about whichever class ran before this one instead.
    /// </remarks>
    private async Task<long> AccountsForAsync(string email) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.tenant WHERE lower(email) = lower('{email}')");

    private TestAuthenticator NewAuthenticator()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        return authenticator;
    }
}
