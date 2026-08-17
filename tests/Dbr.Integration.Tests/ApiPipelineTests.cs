// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// The API as a client meets it: over HTTP, through the real pipeline, with a real
/// authenticator answering real challenges.
/// </summary>
/// <remarks>
/// <para>
/// Everything below the endpoints already has tests. What had none was the part a
/// client actually touches — status codes, the JSON on the wire, whether a route
/// requires a token and refuses without one. That layer is where a mistake looks like
/// nothing at all from inside the application: a route that forgot
/// <c>RequireAuthorization</c> still passes every test written against the service
/// behind it.
/// </para>
/// <para>
/// The WebAuthn payloads here are the ones a browser sends, base64url and all, so
/// these also stand behind the claim that the serialisation on the wire is right —
/// which until now rested on a unit test asserting the shape rather than on anything
/// having parsed it.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ApiPipelineTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    public ValueTask InitializeAsync()
    {
        // A real key manager, because opening an account now creates its profile and a
        // profile is encrypted before it is stored. Signing up is the first thing almost
        // every test here does, so there is no version of these tests that does not need
        // one.
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
    public async Task An_account_is_opened_and_signed_in_by_one_ceremony()
    {
        var email = Unique();

        var session = await SignUpAsync(email, NewAuthenticator());

        Assert.False(string.IsNullOrEmpty(session.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(session.GetProperty("refreshToken").GetString()));

        // Three parts, so what came back is a JWT and not a message about one.
        Assert.Equal(3, session.GetProperty("accessToken").GetString()!.Split('.').Length);
    }

    [Fact]
    public async Task A_token_is_what_opens_an_authenticated_route()
    {
        var email = Unique();
        var session = await SignUpAsync(email, NewAuthenticator());

        var (status, account) = await GetAsync("/api/v1/account", AccessToken(session));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(email, account.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("/api/v1/account")]
    [InlineData("/api/v1/account/passkeys")]
    public async Task An_authenticated_route_refuses_a_request_with_no_token(string path)
    {
        // The mistake this catches is a route that forgot to require authorization,
        // which is invisible from inside the application: the service behind it would
        // pass every test it has, and simply act for nobody.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, null)).Status);
    }

    [Fact]
    public async Task A_token_this_service_did_not_sign_is_refused()
    {
        var session = await SignUpAsync(Unique(), NewAuthenticator());
        var tampered = AccessToken(session)[..^6] + "AAAAAA";

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/account", tampered)).Status);
    }

    [Fact]
    public async Task Sign_in_asks_for_no_identifier_and_takes_no_body()
    {
        // The property the whole design rests on, asserted where a client would see
        // it: this request carries nothing, so it cannot distinguish an address that
        // has an account from one that does not.
        var (status, options) = await PostAsync("/api/v1/auth/login/options", new { }, null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, options.GetProperty("publicKey").GetProperty("allowCredentials").GetArrayLength());
    }

    [Fact]
    public async Task A_passkey_signs_its_account_back_in_over_the_wire()
    {
        var email = Unique();
        var authenticator = NewAuthenticator();
        var signup = await SignUpAsync(email, authenticator);

        var session = await SignInAsync(authenticator, UserHandle(signup));

        var (status, account) = await GetAsync("/api/v1/account", AccessToken(session));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(email, account.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Refreshing_rotates_and_the_spent_token_takes_the_session_with_it()
    {
        var session = await SignUpAsync(Unique(), NewAuthenticator());
        var refreshToken = session.GetProperty("refreshToken").GetString()!;

        var (status, renewed) = await PostAsync("/api/v1/auth/refresh", new { refreshToken }, null);
        Assert.Equal(HttpStatusCode.OK, status);

        var second = renewed.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(refreshToken, second);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/account", AccessToken(renewed))).Status);

        // The spent one comes back, which is what a stolen token looks like from here.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostAsync("/api/v1/auth/refresh", new { refreshToken }, null)).Status);

        // And the session it belonged to is gone, including for whoever holds the
        // newest token.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostAsync("/api/v1/auth/refresh", new { refreshToken = second }, null)).Status);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_says_nothing_about_what_was_presented()
    {
        var session = await SignUpAsync(Unique(), NewAuthenticator());
        var refreshToken = session.GetProperty("refreshToken").GetString()!;

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await PostAsync("/api/v1/auth/logout", new { refreshToken }, null)).Status);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostAsync("/api/v1/auth/refresh", new { refreshToken }, null)).Status);

        // An invented token answers identically, so this cannot be used to ask whether
        // a token found somewhere is worth anything.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await PostAsync("/api/v1/auth/logout", new { refreshToken = "not-a-token" }, null)).Status);
    }

    [Fact]
    public async Task A_second_passkey_is_added_and_signs_the_account_in_by_itself()
    {
        var email = Unique();
        var first = NewAuthenticator();
        var signup = await SignUpAsync(email, first);
        var accessToken = AccessToken(signup);

        var (optionsStatus, options) = await PostAsync("/api/v1/account/passkeys/options", new { }, accessToken);
        Assert.Equal(HttpStatusCode.OK, optionsStatus);

        // The passkey already on the account, so an authenticator holding it declines.
        Assert.Equal(1, options.GetProperty("publicKey").GetProperty("excludeCredentials").GetArrayLength());

        var second = NewAuthenticator();
        var (addStatus, _) = await PostAsync(
            "/api/v1/account/passkeys",
            new
            {
                ceremonyId = options.GetProperty("ceremonyId").GetString(),
                credential = second.Register(
                    Fido2NetLib.CredentialCreateOptions.FromJson(options.GetProperty("publicKey").GetRawText()),
                    DbrApiFactory.Origin),
            },
            accessToken);

        Assert.Equal(HttpStatusCode.OK, addStatus);

        var (listStatus, listed) = await GetAsync("/api/v1/account/passkeys", accessToken);
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(2, listed.GetArrayLength());

        // The point of the exercise: the new one gets in on its own.
        var session = await SignInAsync(second, UserHandle(signup));

        Assert.Equal(email, (await GetAsync("/api/v1/account", AccessToken(session))).Body
            .GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_suspended_account_is_told_why_it_cannot_sign_in()
    {
        var authenticator = NewAuthenticator();
        var signup = await SignUpAsync(Unique(), authenticator);

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.tenant SET status = 'suspended' WHERE id = '{signup.GetProperty("tenantId").GetString()}'");

        var (_, options) = await PostAsync("/api/v1/auth/login/options", new { }, null);

        var (status, _) = await PostAsync(
            "/api/v1/auth/login",
            new
            {
                ceremonyId = options.GetProperty("ceremonyId").GetString(),
                credential = authenticator.Assert(
                    Fido2NetLib.AssertionOptions.FromJson(options.GetProperty("publicKey").GetRawText()),
                    DbrApiFactory.Origin,
                    UserHandle(signup)),
            },
            null);

        // Forbidden rather than unauthorized: the passkey was accepted, and it is the
        // account that may not act.
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Signing_up_without_an_address_is_refused()
    {
        var (status, _) = await PostAsync("/api/v1/auth/register/options", new { email = "  " }, null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    private static string Unique() => $"api-{Guid.NewGuid():N}@example.com";

    private static string AccessToken(JsonElement session) => ApiClient.AccessToken(session);

    private static byte[] UserHandle(JsonElement signup) => ApiClient.UserHandle(signup);

    private TestAuthenticator NewAuthenticator()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        return authenticator;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path,
        object payload,
        string? accessToken) =>
        await _api.PostAsync(path, payload, accessToken);

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, string? accessToken) =>
        await _api.GetAsync(path, accessToken);

    private async Task<JsonElement> SignUpAsync(string email, TestAuthenticator authenticator) =>
        await _api.SignUpAsync(email, authenticator);

    private async Task<JsonElement> SignInAsync(TestAuthenticator authenticator, byte[] userHandle) =>
        await _api.SignInAsync(authenticator, userHandle);
}
