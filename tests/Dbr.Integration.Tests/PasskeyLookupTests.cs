// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Identity;
using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The one way through the tenant boundary, and the shape of the hole it makes.
/// </summary>
/// <remarks>
/// Signing in is the one operation that has to read a tenant-scoped table before it
/// knows which tenant it is acting for, so <c>app.find_passkey</c> is a deliberate
/// exception to the rule everything else in this schema obeys. An exception worth
/// making is one worth pinning down: the tests below assert not only that it works
/// but that it stayed narrow, because the failure mode is somebody widening it later
/// for a good local reason.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PasskeyLookupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly byte[] KnownCredentialId = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] KnownPublicKey = [9, 9, 9];

    private ServiceProvider _services = null!;

    private Guid _tenantId;

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();
        _tenantId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.tenant (id, email) VALUES ('{_tenantId}', 'holder@example.com');
             INSERT INTO public.passkey
                 (tenant_id, credential_id, public_key, signature_count, is_backup_eligible, is_backed_up)
             VALUES
                 ('{_tenantId}', '\x0102030405060708', '\x090909', 7, true, true);
             """);
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync("DELETE FROM public.tenant;");

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task A_passkey_resolves_to_its_account_before_anyone_has_authenticated()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        var found = await scope.ServiceProvider.GetRequiredService<PasskeyLookup>()
            .FindAsync(KnownCredentialId, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(_tenantId, found.TenantId);
        Assert.Equal(KnownPublicKey, found.PublicKey);
        Assert.Equal(7, found.SignatureCount);
    }

    [Fact]
    public async Task The_table_itself_stays_invisible_to_the_same_caller()
    {
        // The contrast that gives the test above its meaning. Both run as the same
        // unauthenticated caller over the same connection; the ordinary query returns
        // nothing because the policy matches nothing, and only the definer function
        // gets an answer. Without this, the lookup succeeding might just mean the
        // boundary was never there.
        using var scope = PostgresFixture.ScopeFor(_services, null);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        Assert.Empty(await context.Set<Passkey>()
            .IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_credential_resolves_to_nothing()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        Assert.Null(await scope.ServiceProvider.GetRequiredService<PasskeyLookup>()
            .FindAsync([255, 255, 255], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_answers_with_nothing_an_unauthenticated_caller_should_not_have()
    {
        // Pinned deliberately. Every column added here is something the sign-in path
        // hands out before any signature has been checked — an address would make this
        // an account-enumeration oracle, and account status would say whether a
        // suspended account exists. Widening it should mean editing this test and
        // saying why.
        var signature = await postgres.QueryAsOwnerAsync<string>(
            """
            SELECT pg_get_function_result(p.oid)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'app' AND p.proname = 'find_passkey'
            """);

        Assert.Equal(
            "TABLE(tenant_id uuid, public_key bytea, signature_count bigint)",
            signature);
    }

    [Fact]
    public async Task It_runs_as_its_owner_over_a_search_path_a_caller_cannot_influence()
    {
        // Both halves matter. Without SECURITY DEFINER the function is subject to the
        // same policies as the caller and returns nothing, so sign-in breaks loudly.
        // Without the fixed search_path it still works — and becomes a way to run
        // chosen code as the owning role, which is the quiet failure.
        Assert.True(await postgres.QueryAsOwnerAsync<bool>(
            """
            SELECT p.prosecdef
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'app' AND p.proname = 'find_passkey'
            """));

        var configuration = await postgres.QueryAsOwnerAsync<string[]>(
            """
            SELECT p.proconfig
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'app' AND p.proname = 'find_passkey'
            """);

        Assert.NotNull(configuration);
        Assert.Equal(["search_path=pg_catalog"], configuration);
    }

    [Fact]
    public async Task Only_the_application_role_may_call_it()
    {
        Assert.True(await postgres.QueryAsOwnerAsync<bool>(
            "SELECT has_function_privilege('dbr_app', 'app.find_passkey(bytea)', 'execute')"));

        // PUBLIC gets EXECUTE on a new function by default, which would put this
        // within reach of any role somebody adds later.
        Assert.False(await postgres.QueryAsOwnerAsync<bool>(
            "SELECT has_function_privilege('public', 'app.find_passkey(bytea)', 'execute')"));
    }

    [Fact]
    public async Task A_unit_of_work_that_reads_before_authenticating_can_still_write_afterwards()
    {
        // The awkward shape signing in has, isolated. The first read happens with no
        // tenant, and the write that follows happens as one — on the same context.
        // It works because the interceptor writes the tenant on every connection open
        // and EF returns the connection to the pool between operations; a connection
        // held open across the two would still be carrying the empty tenant it opened
        // with, and the write would be refused by a policy nobody had violated.
        using var scope = PostgresFixture.ScopeFor(_services, null);
        var provider = scope.ServiceProvider;

        Assert.NotNull(await provider.GetRequiredService<PasskeyLookup>()
            .FindAsync(KnownCredentialId, TestContext.Current.CancellationToken));

        var newTenantId = Guid.NewGuid();
        provider.GetRequiredService<Dbr.Infrastructure.Tenancy.TenantContext>().SetTenant(newTenantId);

        var context = provider.GetRequiredService<DbrDbContext>();

        context.Set<Tenant>().Add(new Tenant
        {
            Id = newTenantId,
            Email = "after@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.tenant WHERE id = '{newTenantId}'"));
    }
}
