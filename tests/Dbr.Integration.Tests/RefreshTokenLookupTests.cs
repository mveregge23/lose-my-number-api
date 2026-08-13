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
/// The second way through the tenant boundary, held to the same shape as the first.
/// </summary>
/// <remarks>
/// Refreshing has the same problem signing in does: the caller's access token has
/// expired, so they act for no tenant, so the row identifying them is invisible. That
/// makes <c>app.find_refresh_token</c> a second deliberate exception — and two is
/// where a pattern starts being copied without the reasoning, so the properties that
/// make it defensible are asserted here rather than assumed to travel with it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class RefreshTokenLookupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly byte[] KnownTokenHash = [1, 2, 3, 4, 5, 6, 7, 8];

    private ServiceProvider _services = null!;

    private Guid _tenantId;

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();
        _tenantId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.tenant (id, email) VALUES ('{_tenantId}', 'refresher@example.com');
             INSERT INTO public.refresh_token
                 (tenant_id, token_hash, session_id, session_started_at, expires_at)
             VALUES
                 ('{_tenantId}', '\x0102030405060708', gen_random_uuid(), now(), now() + interval '30 days');
             """);
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync("DELETE FROM public.tenant;");

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task A_token_resolves_to_its_session_before_anyone_has_authenticated()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        var found = await scope.ServiceProvider.GetRequiredService<RefreshTokenLookup>()
            .FindAsync(KnownTokenHash, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(_tenantId, found.TenantId);
        Assert.Null(found.UsedAt);
        Assert.Null(found.RevokedAt);
    }

    [Fact]
    public async Task The_table_itself_stays_invisible_to_the_same_caller()
    {
        // The contrast that gives the test above its meaning: same caller, same
        // connection, and an ordinary query sees nothing.
        using var scope = PostgresFixture.ScopeFor(_services, null);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        Assert.Empty(await context.Set<RefreshToken>()
            .IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_token_resolves_to_nothing()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);

        Assert.Null(await scope.ServiceProvider.GetRequiredService<RefreshTokenLookup>()
            .FindAsync([255, 255, 255], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_answers_with_session_state_and_nothing_about_the_account()
    {
        // Pinned for the same reason the passkey lookup's signature is. Everything
        // here is handed to a caller who has presented a string and proved nothing
        // else; the account id is the most that can be justified, and an address or a
        // status would make this a way to ask questions about accounts.
        var signature = await postgres.QueryAsOwnerAsync<string>(
            """
            SELECT pg_get_function_result(p.oid)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'app' AND p.proname = 'find_refresh_token'
            """);

        Assert.Equal(
            "TABLE(id uuid, tenant_id uuid, session_id uuid, session_started_at timestamp with time zone, "
            + "expires_at timestamp with time zone, used_at timestamp with time zone, "
            + "revoked_at timestamp with time zone)",
            signature);
    }

    [Fact]
    public async Task It_runs_as_its_owner_over_a_search_path_a_caller_cannot_influence()
    {
        Assert.True(await postgres.QueryAsOwnerAsync<bool>(
            """
            SELECT p.prosecdef
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'app' AND p.proname = 'find_refresh_token'
            """));

        var configuration = await postgres.QueryAsOwnerAsync<string[]>(
            """
            SELECT p.proconfig
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'app' AND p.proname = 'find_refresh_token'
            """);

        Assert.NotNull(configuration);
        Assert.Equal(["search_path=pg_catalog"], configuration);
    }

    [Fact]
    public async Task Only_the_application_role_may_call_it()
    {
        Assert.True(await postgres.QueryAsOwnerAsync<bool>(
            "SELECT has_function_privilege('dbr_app', 'app.find_refresh_token(bytea)', 'execute')"));

        Assert.False(await postgres.QueryAsOwnerAsync<bool>(
            "SELECT has_function_privilege('public', 'app.find_refresh_token(bytea)', 'execute')"));
    }

    [Fact]
    public async Task Nothing_else_has_been_let_through_the_boundary()
    {
        // The count is the test. Each of these is a deliberate hole in the tenant
        // boundary with an argument behind it; a third appearing without this failing
        // would mean one was added without anyone weighing that argument again.
        var definers = await postgres.QueryAsOwnerAsync<string[]>(
            """
            SELECT array_agg(p.proname ORDER BY p.proname)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prosecdef
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            """);

        Assert.NotNull(definers);
        Assert.Equal(["find_passkey", "find_refresh_token"], definers);
    }
}
