// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;
using Dbr.Integration.Tests.Fixtures;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// Adding a second way into an account that already exists.
/// </summary>
/// <remarks>
/// The account this acts on is never named in a request — it comes from the token, and
/// in these tests from the tenant the scope was opened for, which is the same thing one
/// layer down. Most of what is worth testing here is what happens when that account and
/// the ceremony's account are not the same.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PasskeyAdditionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Origin = "http://localhost:8080";

    /// <summary>
    /// Every authenticator these tests create, disposed together at the end. They hold
    /// a key apiece, and tracking them here keeps the tests from having to decide
    /// which ones outlive the block that made them.
    /// </summary>
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
    public async Task An_account_can_be_given_a_second_way_in()
    {
        var (tenantId, _) = await SignUpAsync("two-keys@example.com");

        var second = NewAuthenticator();
        Assert.Equal(PasskeyAdditionOutcome.Added, (await AddAsync(tenantId, second)).Outcome);

        Assert.Equal(2, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.passkey WHERE tenant_id = '{tenantId}'"));
    }

    [Fact]
    public async Task The_second_passkey_can_sign_the_account_in_on_its_own()
    {
        // The test that matters. A passkey that registers but cannot sign in is not a
        // second way in, and everything about registering it would look fine.
        var (tenantId, _) = await SignUpAsync("really-works@example.com");

        var second = NewAuthenticator();
        await AddAsync(tenantId, second);

        var login = await SignInAsync(second, tenantId);

        Assert.Equal(PasskeyLoginOutcome.Authenticated, login.Outcome);
        Assert.Equal(tenantId, login.TenantId);
    }

    [Fact]
    public async Task The_original_passkey_still_works_afterwards()
    {
        var (tenantId, first) = await SignUpAsync("both-work@example.com");

        var second = NewAuthenticator();
        await AddAsync(tenantId, second);

        Assert.Equal(PasskeyLoginOutcome.Authenticated, (await SignInAsync(first, tenantId)).Outcome);
    }

    [Fact]
    public async Task The_challenge_names_the_passkeys_the_account_already_has()
    {
        // What stops an authenticator creating a second credential that does the same
        // job as one it already holds. Without this, tapping the same key twice leaves
        // two rows nobody can tell apart.
        var (tenantId, first) = await SignUpAsync("exclusions@example.com");

        using var scope = PostgresFixture.ScopeFor(_services, tenantId);

        var ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .BeginAdditionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [first.CredentialId],
            ceremony.Options.ExcludeCredentials.Select(descriptor => descriptor.Id));
    }

    [Fact]
    public async Task A_challenge_issued_to_one_account_cannot_add_a_passkey_to_another()
    {
        // Everything about the second request is valid — a real ceremony, a real
        // signature over it, a real signed-in account. The only thing wrong is that
        // they are not the same account, and nothing else in the flow would notice.
        var (victimId, _) = await SignUpAsync("victim@example.com");
        var (attackerId, _) = await SignUpAsync("attacker@example.com");

        PasskeyChallenge<CredentialCreateOptions> ceremony;

        using (var scope = PostgresFixture.ScopeFor(_services, victimId))
        {
            ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .BeginAdditionAsync(TestContext.Current.CancellationToken);
        }

        var attackerKey = NewAuthenticator();

        using (var scope = PostgresFixture.ScopeFor(_services, attackerId))
        {
            var result = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .CompleteAdditionAsync(
                    ceremony.CeremonyId,
                    attackerKey.Register(ceremony.Options, Origin),
                    TestContext.Current.CancellationToken);

            Assert.Equal(PasskeyAdditionOutcome.WrongAccount, result.Outcome);
        }

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.passkey WHERE tenant_id = '{victimId}'"));
    }

    [Fact]
    public async Task A_signup_challenge_cannot_be_finished_as_an_addition()
    {
        // The account a signup ceremony names does not exist yet, so it can never be
        // the account making this request — the same check covers it.
        var (tenantId, _) = await SignUpAsync("holder@example.com");

        PasskeyChallenge<CredentialCreateOptions> signup;

        using (var scope = PostgresFixture.ScopeFor(_services, null))
        {
            signup = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .BeginSignupAsync("elsewhere@example.com", TestContext.Current.CancellationToken);
        }

        var authenticator = NewAuthenticator();
        using var scope2 = PostgresFixture.ScopeFor(_services, tenantId);

        var result = await scope2.ServiceProvider.GetRequiredService<PasskeyService>()
            .CompleteAdditionAsync(
                signup.CeremonyId,
                authenticator.Register(signup.Options, Origin),
                TestContext.Current.CancellationToken);

        Assert.Equal(PasskeyAdditionOutcome.WrongAccount, result.Outcome);
    }

    [Fact]
    public async Task A_passkey_already_registered_here_is_refused()
    {
        var (tenantId, first) = await SignUpAsync("duplicate@example.com");

        // The same authenticator, and therefore the same credential id, offered again.
        // A real authenticator would decline because of the exclusion list; this one
        // does as it is told, which is what makes it useful for testing the server.
        Assert.Equal(PasskeyAdditionOutcome.AttestationRejected, (await AddAsync(tenantId, first)).Outcome);

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.passkey WHERE tenant_id = '{tenantId}'"));
    }

    [Fact]
    public async Task A_passkey_registered_to_somebody_else_is_refused_without_saying_whose()
    {
        var (_, theirs) = await SignUpAsync("owner@example.com");
        var (mineId, _) = await SignUpAsync("interloper@example.com");

        // Registered to an account this one cannot see, so nothing here could have
        // found it by looking — the answer comes from the narrow lookup, and the
        // global unique index is what makes it a guarantee.
        Assert.Equal(PasskeyAdditionOutcome.AttestationRejected, (await AddAsync(mineId, theirs)).Outcome);
    }

    [Fact]
    public async Task The_list_shows_only_this_account_s_passkeys()
    {
        var (mineId, _) = await SignUpAsync("mine@example.com");
        await SignUpAsync("theirs@example.com");

        var second = NewAuthenticator();
        await AddAsync(mineId, second);

        using var scope = PostgresFixture.ScopeFor(_services, mineId);

        var listed = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
            .ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, listed.Count);
        Assert.All(listed, passkey => Assert.Equal(mineId, passkey.TenantId));
    }

    [Fact]
    public async Task Adding_a_passkey_without_being_signed_in_is_a_mistake_rather_than_an_empty_result()
    {
        // Every other read in this codebase fails closed by returning nothing. This
        // one cannot: "add a passkey to whichever account this is" has no meaning, and
        // an unset tenant here means a route was reached without requiring a token.
        using var scope = PostgresFixture.ScopeFor(_services, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .BeginAdditionAsync(TestContext.Current.CancellationToken));
    }

    private static byte[] UserHandle(Guid tenantId) => tenantId.ToByteArray(bigEndian: true);

    private TestAuthenticator NewAuthenticator()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        return authenticator;
    }

    private async Task<(Guid TenantId, TestAuthenticator Authenticator)> SignUpAsync(string email)
    {
        var authenticator = NewAuthenticator();

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

    /// <summary>
    /// Both legs as the signed-in account, which is what an authenticated request is
    /// one layer up.
    /// </summary>
    private async Task<PasskeyAdditionResult> AddAsync(Guid tenantId, TestAuthenticator authenticator)
    {
        PasskeyChallenge<CredentialCreateOptions> ceremony;

        using (var scope = PostgresFixture.ScopeFor(_services, tenantId))
        {
            ceremony = await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .BeginAdditionAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = PostgresFixture.ScopeFor(_services, tenantId))
        {
            return await scope.ServiceProvider.GetRequiredService<PasskeyService>()
                .CompleteAdditionAsync(
                    ceremony.CeremonyId,
                    authenticator.Register(ceremony.Options, Origin),
                    TestContext.Current.CancellationToken);
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
}
