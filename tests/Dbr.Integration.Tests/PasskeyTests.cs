// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Identity;
using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// Opening an account with a passkey and signing back in with it, against a real
/// database and real signatures.
/// </summary>
/// <remarks>
/// Every ceremony below runs its two halves in separate scopes, because that is what
/// they are in production: two HTTP requests, each with its own unit of work and its
/// own tenant context. Running both halves in one scope would quietly test a
/// arrangement that never happens — and would step over the awkward part, which is
/// that the first half runs before anyone is authenticated and the second half has to
/// start acting for an account partway through.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PasskeyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Origin = "http://localhost:8080";

    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // The credential rows go with the accounts they belong to, by cascade.
        await postgres.ExecuteAsOwnerAsync(
            "DELETE FROM public.tenant; DELETE FROM public.passkey_ceremony;");

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Registering_a_passkey_opens_the_account_it_was_registered_for()
    {
        using var authenticator = new TestAuthenticator();

        var tenantId = await SignUpAsync("new@example.com", authenticator);

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.tenant WHERE id = '{tenantId}'"));

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.passkey WHERE tenant_id = '{tenantId}'"));
    }

    [Fact]
    public async Task Nothing_is_written_until_the_authenticator_has_answered()
    {
        // An abandoned signup must not leave an account behind: a row nobody holds a
        // credential for is an address that can never be registered again and can
        // never be signed in to.
        using var scope = PostgresFixture.ScopeFor(_services, null);

        await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .BeginSignupAsync("abandoned@example.com", TestContext.Current.CancellationToken);

        Assert.Equal(0, await postgres.QueryAsOwnerAsync<long>("SELECT count(*) FROM public.tenant"));
    }

    [Fact]
    public async Task A_registered_passkey_signs_its_account_back_in()
    {
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("returning@example.com", authenticator);

        var result = await SignInAsync(authenticator, tenantId);

        Assert.Equal(PasskeyLoginOutcome.Authenticated, result.Outcome);

        // The account was worked out from the credential alone. Nothing in this test
        // told the sign-in path an address, an id, or anything else about who was
        // arriving.
        Assert.Equal(tenantId, result.TenantId);
    }

    [Fact]
    public async Task Signing_in_asks_for_no_credential_in_particular()
    {
        // The empty allow-list is what makes the response to this identical for
        // somebody with an account and somebody without one. If it ever named
        // credentials, the request that precedes it would have to name an account,
        // and the endpoint would become a way to ask whether an address is registered.
        using var scope = PostgresFixture.ScopeFor(_services, null);

        var ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .BeginLoginAsync(TestContext.Current.CancellationToken);

        Assert.Empty(ceremony.Options.AllowCredentials);
    }

    [Fact]
    public async Task A_signature_from_the_wrong_key_is_refused()
    {
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("target@example.com", authenticator);

        var (ceremonyId, options) = await BeginLoginAsync();
        var forged = authenticator.AssertWithTheWrongKey(options, Origin, UserHandle(tenantId));

        using var scope = PostgresFixture.ScopeFor(_services, null);

        var result = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .CompleteLoginAsync(ceremonyId, forged, TestContext.Current.CancellationToken);

        Assert.Equal(PasskeyLoginOutcome.AssertionRejected, result.Outcome);
    }

    [Fact]
    public async Task An_assertion_meant_for_one_account_cannot_sign_in_another()
    {
        // The authenticator says which account it believes the credential belongs to.
        // Believing it without checking would mean a valid signature from any passkey
        // could name any account.
        using var authenticator = new TestAuthenticator();
        await SignUpAsync("owner@example.com", authenticator);

        using var other = new TestAuthenticator();
        var victimId = await SignUpAsync("victim@example.com", other);

        var (ceremonyId, options) = await BeginLoginAsync();
        var assertion = authenticator.Assert(options, Origin, UserHandle(victimId));

        using var scope = PostgresFixture.ScopeFor(_services, null);

        var result = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .CompleteLoginAsync(ceremonyId, assertion, TestContext.Current.CancellationToken);

        Assert.Equal(PasskeyLoginOutcome.AssertionRejected, result.Outcome);
    }

    [Fact]
    public async Task A_challenge_is_answered_once_and_only_once()
    {
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("replay@example.com", authenticator);

        var (ceremonyId, options) = await BeginLoginAsync();
        var assertion = authenticator.Assert(options, Origin, UserHandle(tenantId));

        Assert.Equal(PasskeyLoginOutcome.Authenticated, (await CompleteLoginAsync(ceremonyId, assertion)).Outcome);

        // The same signature, sent again. Without the ceremony being spent this would
        // verify perfectly well the second time — it is a valid signature over a
        // challenge this server did issue.
        Assert.Equal(
            PasskeyLoginOutcome.CeremonyUnusable,
            (await CompleteLoginAsync(ceremonyId, assertion)).Outcome);
    }

    [Fact]
    public async Task A_challenge_stops_being_accepted_once_it_has_expired()
    {
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("stale@example.com", authenticator);

        var (ceremonyId, options) = await BeginLoginAsync();
        var assertion = authenticator.Assert(options, Origin, UserHandle(tenantId));

        // Reaching into the row rather than waiting out a real expiry: the behaviour
        // under test is what happens after the deadline, not how long the deadline is.
        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.passkey_ceremony SET expires_at = now() - interval '1 second' "
            + $"WHERE id = '{ceremonyId}'");

        Assert.Equal(
            PasskeyLoginOutcome.CeremonyUnusable,
            (await CompleteLoginAsync(ceremonyId, assertion)).Outcome);
    }

    [Fact]
    public async Task A_registration_challenge_cannot_be_answered_as_a_sign_in()
    {
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("confused@example.com", authenticator);

        using var scope = PostgresFixture.ScopeFor(_services, null);

        var registration = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .BeginSignupAsync("second@example.com", TestContext.Current.CancellationToken);

        var (_, loginOptions) = await BeginLoginAsync();
        var assertion = authenticator.Assert(loginOptions, Origin, UserHandle(tenantId));

        Assert.Equal(
            PasskeyLoginOutcome.CeremonyUnusable,
            (await CompleteLoginAsync(registration.CeremonyId, assertion)).Outcome);
    }

    [Fact]
    public async Task An_authenticator_whose_counter_stops_advancing_is_refused()
    {
        // The one thing a copied authenticator cannot fake. Both halves of a cloned
        // pair count from where they were copied, so the original's next assertion
        // eventually reports a number the server has already seen.
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("cloned@example.com", authenticator);

        Assert.Equal(PasskeyLoginOutcome.Authenticated, (await SignInAsync(authenticator, tenantId)).Outcome);

        authenticator.SignCount = 0;

        Assert.Equal(PasskeyLoginOutcome.AssertionRejected, (await SignInAsync(authenticator, tenantId)).Outcome);
    }

    [Fact]
    public async Task A_successful_sign_in_records_what_the_assertion_reported()
    {
        using var authenticator = new TestAuthenticator();
        var tenantId = await SignUpAsync("recorded@example.com", authenticator);

        await SignInAsync(authenticator, tenantId);

        using var scope = PostgresFixture.ScopeFor(_services, tenantId);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var credential = await context.Set<Passkey>()
            .SingleAsync(TestContext.Current.CancellationToken);

        // Without this the counter never moves, and the clone detection above is
        // permanently disarmed while appearing to work.
        Assert.Equal(authenticator.SignCount, (uint)credential.SignatureCount);
        Assert.NotNull(credential.LastUsedAt);
    }

    [Fact]
    public async Task An_address_cannot_be_registered_twice()
    {
        using var first = new TestAuthenticator();
        await SignUpAsync("taken@example.com", first);

        using var second = new TestAuthenticator();
        var ceremony = await BeginSignUpAsync("taken@example.com");

        using var scope = PostgresFixture.ScopeFor(_services, null);

        var result = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .CompleteSignupAsync(
                ceremony.CeremonyId,
                second.Register(ceremony.Options, Origin),
                TestContext.Current.CancellationToken);

        Assert.Equal(PasskeySignupOutcome.AddressAlreadyRegistered, result.Outcome);

        // Nothing half-written: the account and its passkey go in together or not at
        // all, so a rejected duplicate must not leave a credential behind.
        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(*) FROM public.passkey"));
    }

    [Fact]
    public async Task A_passkey_is_invisible_to_every_other_account()
    {
        using var authenticator = new TestAuthenticator();
        await SignUpAsync("mine@example.com", authenticator);

        using var other = new TestAuthenticator();
        var otherId = await SignUpAsync("theirs@example.com", other);

        using var scope = PostgresFixture.ScopeFor(_services, otherId);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var visible = await context.Set<Passkey>()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([otherId], visible.Select(credential => credential.TenantId));
    }

    /// <summary>
    /// The account handle a discoverable credential stores and hands back, which is
    /// how a sign-in that asked nobody anything works out whose account it is.
    /// </summary>
    private static byte[] UserHandle(Guid tenantId) => tenantId.ToByteArray(bigEndian: true);

    private async Task<PasskeyChallenge<CredentialCreateOptions>> BeginSignUpAsync(string email)
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        return await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .BeginSignupAsync(email, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SignUpAsync(string email, TestAuthenticator authenticator)
    {
        var ceremony = await BeginSignUpAsync(email);

        using var scope = PostgresFixture.ScopeFor(_services, null);

        var result = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .CompleteSignupAsync(
                ceremony.CeremonyId,
                authenticator.Register(ceremony.Options, Origin),
                TestContext.Current.CancellationToken);

        Assert.Equal(PasskeySignupOutcome.Created, result.Outcome);

        return result.TenantId;
    }

    private async Task<(Guid CeremonyId, AssertionOptions Options)> BeginLoginAsync()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        var ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .BeginLoginAsync(TestContext.Current.CancellationToken);

        return (ceremony.CeremonyId, ceremony.Options);
    }

    private async Task<PasskeyLoginResult> CompleteLoginAsync(
        Guid ceremonyId,
        AuthenticatorAssertionRawResponse assertion)
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        return await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .CompleteLoginAsync(ceremonyId, assertion, TestContext.Current.CancellationToken);
    }

    private async Task<PasskeyLoginResult> SignInAsync(TestAuthenticator authenticator, Guid tenantId)
    {
        var (ceremonyId, options) = await BeginLoginAsync();

        return await CompleteLoginAsync(ceremonyId, authenticator.Assert(options, Origin, UserHandle(tenantId)));
    }
}
