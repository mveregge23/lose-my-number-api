// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// The first real table, against the boundary that was built before it existed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantEntityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync("DELETE FROM public.tenant;");
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task An_account_can_be_created_by_the_tenant_it_belongs_to()
    {
        // The signup path: the id is generated first, the caller acts as that tenant,
        // and only then is the row written. The policy's WITH CHECK means a caller can
        // only ever create the account it is already claiming to be — which is a
        // stronger guarantee than an endpoint remembering to compare two values.
        var tenantId = Guid.NewGuid();

        using (var scope = PostgresFixture.ScopeFor(_services, tenantId))
        {
            var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

            context.Set<Tenant>().Add(new Tenant
            {
                Id = tenantId,
                Email = "someone@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.tenant WHERE id = '{tenantId}'"));
    }

    [Fact]
    public async Task An_account_cannot_be_created_for_somebody_else()
    {
        using var scope = PostgresFixture.ScopeFor(_services, Guid.NewGuid());
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        context.Set<Tenant>().Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Email = "not-mine@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.InsufficientPrivilege,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task An_account_is_invisible_to_every_other_tenant()
    {
        var alice = await CreateTenantAsync("alice@example.com");
        await CreateTenantAsync("bob@example.com");

        using var scope = PostgresFixture.ScopeFor(_services, alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var visible = await context.Set<Tenant>().ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([alice], visible.Select(tenant => tenant.Id));
    }

    [Fact]
    public async Task An_unauthenticated_caller_sees_no_accounts_at_all()
    {
        // Not only privacy: without this, an unauthenticated caller could enumerate
        // which addresses have accounts here, which for a service whose users are
        // trying to reduce their exposure is itself the harm.
        await CreateTenantAsync("private@example.com");

        using var scope = PostgresFixture.ScopeFor(_services, null);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        Assert.Empty(await context.Set<Tenant>().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_address_can_only_be_registered_once_whatever_its_casing()
    {
        await CreateTenantAsync("Casing@Example.com");

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateTenantAsync("casing@example.COM"));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Uniqueness_holds_across_tenants_despite_the_isolation()
    {
        // The subtle one. Row-level security hides the existing row from the second
        // caller entirely, so nothing in application code could have found the clash
        // by looking. The unique index is enforced beneath the policy, which is the
        // only reason two people cannot register the same address.
        await CreateTenantAsync("shared@example.com");

        var other = Guid.NewGuid();
        using var scope = PostgresFixture.ScopeFor(_services, other);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        Assert.Empty(await context.Set<Tenant>().ToListAsync(TestContext.Current.CancellationToken));

        context.Set<Tenant>().Add(new Tenant
        {
            Id = other,
            Email = "shared@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task An_account_starts_active_and_can_be_suspended()
    {
        var tenantId = await CreateTenantAsync("status@example.com");

        using (var scope = PostgresFixture.ScopeFor(_services, tenantId))
        {
            var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();
            var tenant = await context.Set<Tenant>().SingleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TenantStatus.Active, tenant.Status);

            tenant.Status = TenantStatus.Suspended;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Read back through the database rather than the tracked instance, so this
        // asserts what was stored — the enum is written as lower-case text to match
        // the column's check constraint, and a mismatch would be rejected here.
        Assert.Equal("suspended", await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.tenant WHERE id = '{tenantId}'"));
    }

    [Fact]
    public async Task An_unknown_status_is_refused_by_the_database()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"INSERT INTO public.tenant (id, email, status) "
                + $"VALUES ('{Guid.NewGuid()}', 'bad-status@example.com', 'deleted')"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private async Task<Guid> CreateTenantAsync(string email)
    {
        var tenantId = Guid.NewGuid();

        using var scope = PostgresFixture.ScopeFor(_services, tenantId);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        context.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId,
            Email = email,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return tenantId;
    }
}
