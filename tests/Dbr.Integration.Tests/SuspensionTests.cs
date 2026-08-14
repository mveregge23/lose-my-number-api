// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;
using Dbr.Integration.Tests.Fixtures;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The base gate: an account that may not act cannot get in, and cannot stay in.
/// </summary>
/// <remarks>
/// Underneath whatever decides whether an account may start new work, because
/// suspension is a different question from monetization and applies in every
/// deployment mode. A self-hosted operator suspending an abusive user of their own
/// instance goes through the same check the hosted instance would.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SuspensionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Origin = "http://localhost:8080";

    private readonly List<TestAuthenticator> _authenticators = [];

    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync(
            "DELETE FROM public.tenant; DELETE FROM public.passkey_ceremony;");

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in_even_with_its_own_passkey()
    {
        var (tenantId, authenticator) = await SignUpAsync("suspended@example.com");

        await SuspendAsync(tenantId);

        var result = await SignInAsync(authenticator, tenantId);

        Assert.Equal(PasskeyLoginOutcome.AccountSuspended, result.Outcome);
    }

    [Fact]
    public async Task A_refused_sign_in_leaves_no_trace_of_having_worked()
    {
        // The assertion verified, so the passkey was used — but it did not get anyone
        // in, and a last-used timestamp saying otherwise would be a small lie in the
        // one place an owner might look to see whether somebody had been using it.
        var (tenantId, authenticator) = await SignUpAsync("no-trace@example.com");

        await SuspendAsync(tenantId);
        await SignInAsync(authenticator, tenantId);

        Assert.Equal(0, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.passkey WHERE tenant_id = '{tenantId}' AND last_used_at IS NOT NULL"));
    }

    [Fact]
    public async Task A_suspended_account_cannot_renew_a_session_it_already_had()
    {
        // The case that matters most. A session outlives the sign-in that made it, so
        // a gate only at sign-in would let an account suspended today keep renewing
        // its access indefinitely on a token it obtained yesterday.
        var (tenantId, authenticator) = await SignUpAsync("mid-session@example.com");

        var session = await StartSessionAsync(authenticator, tenantId);

        await SuspendAsync(tenantId);

        Assert.Equal(SessionRefreshOutcome.AccountSuspended, (await RefreshAsync(session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task Lifting_a_suspension_restores_the_session_that_was_open()
    {
        // Suspension is not deletion. Revoking sessions on the way in would make a
        // reversible measure permanent for whoever happened to be signed in when it
        // was applied.
        var (tenantId, authenticator) = await SignUpAsync("reinstated@example.com");

        var session = await StartSessionAsync(authenticator, tenantId);

        await SuspendAsync(tenantId);
        await RefreshAsync(session.RefreshToken);

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.tenant SET status = 'active' WHERE id = '{tenantId}'");

        var result = await RefreshAsync(session.RefreshToken);

        Assert.Equal(SessionRefreshOutcome.Renewed, result.Outcome);
        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task An_active_account_is_unaffected()
    {
        // The check that keeps the four above from passing for the wrong reason.
        var (tenantId, authenticator) = await SignUpAsync("ordinary@example.com");

        var session = await StartSessionAsync(authenticator, tenantId);

        Assert.Equal(SessionRefreshOutcome.Renewed, (await RefreshAsync(session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task An_access_token_issued_before_the_suspension_keeps_working_until_it_expires()
    {
        // Recorded rather than discovered. Nothing consults the database on an
        // ordinary request — that is what makes ordinary requests cheap — so an access
        // token already in someone's hands is not affected by anything here. The
        // window is the access token's lifetime, the same one a sign-out leaves, and
        // this test exists so that shrinking or closing it is a deliberate change to
        // something written down rather than a surprise.
        var (tenantId, authenticator) = await SignUpAsync("still-holding@example.com");

        var session = await StartSessionAsync(authenticator, tenantId);

        await SuspendAsync(tenantId);

        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(session.AccessToken);

        Assert.Equal(tenantId.ToString(), token.Subject);
        Assert.True(token.ValidTo > DateTime.UtcNow, "the token under test had already expired");
    }

    private static byte[] UserHandle(Guid tenantId) => tenantId.ToByteArray(bigEndian: true);

    private Task SuspendAsync(Guid tenantId) =>
        postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.tenant SET status = 'suspended' WHERE id = '{tenantId}'");

    private async Task<(Guid TenantId, TestAuthenticator Authenticator)> SignUpAsync(string email)
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        PasskeyChallenge<CredentialCreateOptions> ceremony;

        using (var scope = PostgresFixture.ScopeFor(_services, null))
        {
            ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .BeginSignupAsync(email, TestContext.Current.CancellationToken);
        }

        using (var scope = PostgresFixture.ScopeFor(_services, null))
        {
            var result = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .CompleteSignupAsync(
                    ceremony.CeremonyId,
                    authenticator.Register(ceremony.Options, Origin),
                    TestContext.Current.CancellationToken);

            Assert.Equal(PasskeySignupOutcome.Created, result.Outcome);

            return (result.TenantId, authenticator);
        }
    }

    private async Task<PasskeyLoginResult> SignInAsync(TestAuthenticator authenticator, Guid tenantId)
    {
        PasskeyChallenge<AssertionOptions> ceremony;

        using (var scope = PostgresFixture.ScopeFor(_services, null))
        {
            ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .BeginLoginAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = PostgresFixture.ScopeFor(_services, null))
        {
            return await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .CompleteLoginAsync(
                    ceremony.CeremonyId,
                    authenticator.Assert(ceremony.Options, Origin, UserHandle(tenantId)),
                    TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Signs in and takes the tokens, the way the endpoint does.</summary>
    private async Task<IssuedSession> StartSessionAsync(TestAuthenticator authenticator, Guid tenantId)
    {
        Assert.Equal(PasskeyLoginOutcome.Authenticated, (await SignInAsync(authenticator, tenantId)).Outcome);

        using var scope = PostgresFixture.ScopeFor(_services, null);

        return await scope.ServiceProvider.GetRequiredService<SessionService>()
            .StartAsync(tenantId, TestContext.Current.CancellationToken);
    }

    private async Task<SessionRefreshResult> RefreshAsync(string refreshToken)
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        return await scope.ServiceProvider.GetRequiredService<SessionService>()
            .RefreshAsync(refreshToken, TestContext.Current.CancellationToken);
    }
}
