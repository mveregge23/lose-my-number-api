// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Dbr.Domain.Identity;
using Dbr.Infrastructure.Identity;
using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dbr.Integration.Tests;

/// <summary>
/// Sessions: issuing them, rotating them, and taking them away.
/// </summary>
/// <remarks>
/// Each call below runs in its own scope, because each is its own request in
/// production — and because the tenant context is established once per scope, which is
/// exactly the constraint a refresh has to work within: it arrives acting for nobody
/// and has to become an account partway through.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SessionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceProvider _services = null!;

    private Guid _tenantId;

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();
        _tenantId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"INSERT INTO public.tenant (id, email) VALUES ('{_tenantId}', 'session@example.com');");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync("DELETE FROM public.tenant;");

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Signing_in_issues_an_access_token_and_a_refresh_token()
    {
        var session = await StartAsync();

        Assert.NotEmpty(session.AccessToken);
        Assert.NotEmpty(session.RefreshToken);
        Assert.True(session.AccessTokenExpiresAt < session.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task What_is_stored_is_a_digest_and_not_the_token()
    {
        // The property the whole table depends on. A refresh token is a bearer
        // credential, so a dump of this table storing them would be a dump of live
        // sessions — and the only way to be sure it does not is to go looking.
        var session = await StartAsync();

        var stored = await postgres.QueryAsOwnerAsync<byte[]>(
            "SELECT token_hash FROM public.refresh_token LIMIT 1");

        Assert.NotNull(stored);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(session.RefreshToken)), stored);

        Assert.Equal(0, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.refresh_token WHERE encode(token_hash, 'escape') = '{session.RefreshToken}'"));
    }

    [Fact]
    public async Task An_access_token_names_the_account_and_carries_nothing_else()
    {
        // A token is readable by anyone holding it, and it passes through browsers,
        // proxies and logs. An address in here would be handing that to every one of
        // them, so what is inside is worth pinning: an account id, and the timestamps
        // that make it expire.
        var session = await StartAsync();

        var token = new JsonWebToken(session.AccessToken);

        Assert.Equal(
            ["aud", "exp", "iat", "iss", "nbf", "sub"],
            token.Claims.Select(claim => claim.Type).Order(StringComparer.Ordinal));

        Assert.Equal(_tenantId.ToString(), token.Subject);
    }

    [Fact]
    public async Task A_refresh_token_buys_a_new_pair()
    {
        var session = await StartAsync();

        var result = await RefreshAsync(session.RefreshToken);

        Assert.Equal(SessionRefreshOutcome.Renewed, result.Outcome);
        Assert.NotNull(result.Session);
        Assert.NotEqual(session.RefreshToken, result.Session.RefreshToken);
    }

    [Fact]
    public async Task Rotating_keeps_the_session_and_the_clock_it_started()
    {
        // The session is what a sign-out ends and what a stolen token compromises, so
        // it has to survive rotation. Its start time has to survive too — if rotation
        // moved it, the cap on how long a session may live would be extended by the
        // very act it is meant to bound.
        var session = await StartAsync();
        var startedAt = await SessionStartedAtAsync();

        await RefreshAsync(session.RefreshToken);

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(DISTINCT session_id) FROM public.refresh_token"));

        Assert.Equal(startedAt, await SessionStartedAtAsync());
    }

    [Fact]
    public async Task A_spent_refresh_token_stops_working()
    {
        var session = await StartAsync();
        await RefreshAsync(session.RefreshToken);

        Assert.Equal(
            SessionRefreshOutcome.ReusedAndRevoked,
            (await RefreshAsync(session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task Presenting_a_spent_token_ends_the_whole_session()
    {
        // The point of rotation. Two parties holding one token means one of them stole
        // it, and nothing distinguishes them, so the session both are using ends —
        // including for whoever is currently holding the newest token.
        var session = await StartAsync();
        var renewed = await RefreshAsync(session.RefreshToken);

        Assert.Equal(
            SessionRefreshOutcome.ReusedAndRevoked,
            (await RefreshAsync(session.RefreshToken)).Outcome);

        Assert.NotNull(renewed.Session);
        Assert.Equal(
            SessionRefreshOutcome.Rejected,
            (await RefreshAsync(renewed.Session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var session = await StartAsync();

        using (var scope = PostgresFixture.ScopeFor(_services, null))
        {
            await scope.ServiceProvider.GetRequiredService<SessionService>()
                .SignOutAsync(session.RefreshToken, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SessionRefreshOutcome.Rejected, (await RefreshAsync(session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task Signing_out_with_a_token_that_means_nothing_does_nothing_and_says_nothing()
    {
        // Answering differently for a real token than for an invented one would make
        // this endpoint a way to ask whether a token found somewhere is still worth
        // something.
        using var scope = PostgresFixture.ScopeFor(_services, null);

        await scope.ServiceProvider.GetRequiredService<SessionService>()
            .SignOutAsync("not-a-token-anyone-issued", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused()
    {
        var session = await StartAsync();

        await postgres.ExecuteAsOwnerAsync(
            "UPDATE public.refresh_token SET expires_at = now() - interval '1 second'");

        Assert.Equal(SessionRefreshOutcome.Rejected, (await RefreshAsync(session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task A_session_that_has_run_its_full_length_cannot_be_rotated_further()
    {
        // The deadline rotation cannot move. Without it, a session that keeps being
        // refreshed — by its owner or by whoever took it — never ends.
        var session = await StartAsync();

        await postgres.ExecuteAsOwnerAsync(
            "UPDATE public.refresh_token SET session_started_at = now() - interval '365 days'");

        Assert.Equal(SessionRefreshOutcome.Rejected, (await RefreshAsync(session.RefreshToken)).Outcome);
    }

    [Fact]
    public async Task A_refresh_token_is_never_promised_more_life_than_its_session_has()
    {
        // Near the end of a session, a fresh token still gets the standard thirty days
        // on paper unless something clamps it — and a client told it has thirty days
        // when it has one is a client that stops working without warning.
        var session = await StartAsync();

        await postgres.ExecuteAsOwnerAsync(
            "UPDATE public.refresh_token SET session_started_at = now() - interval '89 days'");

        var renewed = await RefreshAsync(session.RefreshToken);

        Assert.NotNull(renewed.Session);
        Assert.True(
            renewed.Session.RefreshTokenExpiresAt < DateTimeOffset.UtcNow.AddDays(2),
            "the new token outlived the session it belongs to");
    }

    [Fact]
    public async Task One_account_cannot_see_another_account_s_sessions()
    {
        await StartAsync();

        var other = Guid.NewGuid();
        await postgres.ExecuteAsOwnerAsync(
            $"INSERT INTO public.tenant (id, email) VALUES ('{other}', 'other@example.com');");

        using var scope = PostgresFixture.ScopeFor(_services, other);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        Assert.Empty(await context.Set<RefreshToken>().ToListAsync(TestContext.Current.CancellationToken));
    }

    private async Task<IssuedSession> StartAsync()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        return await scope.ServiceProvider.GetRequiredService<SessionService>()
            .StartAsync(_tenantId, TestContext.Current.CancellationToken);
    }

    private async Task<SessionRefreshResult> RefreshAsync(string refreshToken)
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        return await scope.ServiceProvider.GetRequiredService<SessionService>()
            .RefreshAsync(refreshToken, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Read as <see cref="DateTime"/> because that is what Npgsql hands back for a
    /// <c>timestamptz</c> unless told otherwise; the comparison it is used for cares
    /// about the instant, not the type.
    /// </summary>
    private Task<DateTime> SessionStartedAtAsync() =>
        postgres.QueryAsOwnerAsync<DateTime>(
            "SELECT max(session_started_at) FROM public.refresh_token")!;
}
