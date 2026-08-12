// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// The tenant boundary, asserted against a real engine through the real application
/// wiring: a query that forgets its tenant filter must return nothing rather than
/// somebody else's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _table = $"isolation_probe_{Guid.NewGuid():N}";
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        await postgres.CreateTenantScopedTableAsync(_table);
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.{_table} (tenant_id, note) VALUES
                 ('{Alice}', 'alice note'),
                 ('{Bob}', 'bob note');
             """);
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync($"DROP TABLE IF EXISTS public.{_table};");
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_rows()
    {
        Assert.Equal(["alice note"], await NotesVisibleToAsync(Alice));
        Assert.Equal(["bob note"], await NotesVisibleToAsync(Bob));
    }

    [Fact]
    public async Task A_scope_with_no_tenant_sees_nothing()
    {
        // The property the whole design rests on. Two rows exist and the query has no
        // filter of any kind — an unidentified connection still gets none of them.
        Assert.Empty(await NotesVisibleToAsync(null));
    }

    [Fact]
    public async Task Naming_another_tenant_explicitly_does_not_reach_it()
    {
        // Not the same test as the one above: this is a query that does exactly what a
        // compromised or buggy caller would do, rather than one that merely omits a
        // filter.
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var leaked = await context.Database
            .SqlQueryRaw<string>($"SELECT note AS \"Value\" FROM public.{_table} WHERE tenant_id = '{Bob}'")
            .ToListAsync();

        Assert.Empty(leaked);
    }

    [Fact]
    public async Task A_tenant_cannot_write_a_row_belonging_to_another()
    {
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database
            .ExecuteSqlRawAsync(
                $"INSERT INTO public.{_table} (tenant_id, note) VALUES ('{Bob}', 'forged')"));

        // Without WITH CHECK on the policy this would succeed, and isolation would be
        // read-only: Alice could write into a tenant she cannot read from.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Equal(2, await RowCountAsOwnerAsync());
    }

    [Fact]
    public async Task A_tenant_cannot_move_its_own_row_to_another_tenant()
    {
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        await Assert.ThrowsAsync<PostgresException>(() => context.Database
            .ExecuteSqlRawAsync($"UPDATE public.{_table} SET tenant_id = '{Bob}'"));

        Assert.Equal(1, await RowCountAsOwnerAsync(Alice));
    }

    [Fact]
    public async Task A_tenant_cannot_delete_another_tenants_rows()
    {
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        // No error here, and that is correct: the rows are invisible, so there is
        // nothing to refuse. What matters is that Bob's row is still there after.
        var affected = await context.Database.ExecuteSqlRawAsync($"DELETE FROM public.{_table}");

        Assert.Equal(1, affected);
        Assert.Equal(1, await RowCountAsOwnerAsync(Bob));
    }

    [Fact]
    public async Task A_reused_pooled_connection_does_not_carry_the_previous_tenant()
    {
        // Npgsql pools by connection string, so the second scope below is very likely
        // handed the physical connection the first one just returned. If the session
        // variable survived that, the result would be a cross-tenant read that looks
        // exactly like a correct one — which is why the interceptor writes the setting
        // on every open rather than only when a tenant is present.
        Assert.Equal(["alice note"], await NotesVisibleToAsync(Alice));

        Assert.Empty(await NotesVisibleToAsync(null));
        Assert.Equal(["bob note"], await NotesVisibleToAsync(Bob));
    }

    [Fact]
    public async Task The_application_acts_as_a_role_that_row_level_security_applies_to()
    {
        // The reason every assertion above means anything. Postgres skips row-level
        // security for superusers, for BYPASSRLS roles, and for a table's owner unless
        // it is FORCEd — and the connection string here belongs to a role that is all
        // three. If SET ROLE ever stopped happening, the policies would still exist,
        // still look right, and isolate nothing.
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var role = await context.Database
            .SqlQueryRaw<string>("SELECT current_user AS \"Value\"")
            .SingleAsync();

        Assert.Equal(TenantSessionInterceptor.ApplicationRole, role);

        var bypassesRls = await context.Database
            .SqlQueryRaw<bool>(
                "SELECT (rolsuper OR rolbypassrls) AS \"Value\" FROM pg_roles WHERE rolname = current_user")
            .SingleAsync();

        Assert.False(bypassesRls);
    }

    private async Task<List<string>> NotesVisibleToAsync(Guid? tenantId)
    {
        using var scope = PostgresFixture.ScopeFor(_services, tenantId);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        return await context.Database
            .SqlQueryRaw<string>($"SELECT note AS \"Value\" FROM public.{_table} ORDER BY note")
            .ToListAsync();
    }

    private async Task<int> RowCountAsOwnerAsync(Guid? tenantId = null)
    {
        var where = tenantId is { } id ? $" WHERE tenant_id = '{id}'" : string.Empty;

        return await postgres.QueryAsOwnerAsync<int>(
            $"SELECT count(*)::int FROM public.{_table}{where}");
    }
}
