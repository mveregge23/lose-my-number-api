// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.Domain.Tenancy;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// What the core store and the vault store have in common: both belong to one tenant at
/// a time, and both narrow every query to it.
/// </summary>
/// <remarks>
/// Shared rather than written twice. The filters are defence in depth, and defence in
/// depth that exists in one store and not the other is worth very little — the store it
/// is missing from is the one holding names and addresses.
/// </remarks>
public abstract class TenantScopedDbContext(DbContextOptions options, ITenantContext tenantContext)
    : DbContext(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(TenantScopedDbContext).GetMethod(
            nameof(ApplyTenantFilter),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// The tenant every filtered query is narrowed to, or <see langword="null"/> when
    /// this unit of work is not acting for one.
    /// </summary>
    /// <remarks>
    /// Public because the query filters read it through the context instance, which is
    /// what makes EF re-evaluate it per query rather than baking one request's tenant
    /// into the cached model.
    /// </remarks>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    /// <summary>
    /// Narrows every <see cref="ITenantScoped"/> entity to the current tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <em>not</em> what keeps tenants apart — the row-level security policies
    /// are, and they hold whether this runs or not. This exists because the policies are
    /// enforced somewhere the application cannot see, by configuration the application
    /// does not control. A revoked <c>FORCE</c>, a connection that reached the database
    /// as the wrong role, a table whose migration forgot to call
    /// <c>app.enable_tenant_rls</c> — each of those turns the database into something
    /// that hands back every tenant's rows, and this makes the application ask for only
    /// its own anyway.
    /// </para>
    /// <para>
    /// Applied by convention rather than per entity, because the failure mode of listing
    /// them by hand is forgetting one, and the symptom of forgetting one is a
    /// cross-tenant read.
    /// </para>
    /// </remarks>
    protected void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // Owned types are filtered through their owner, and a derived type shares
            // its root's filter; EF rejects a filter on either.
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)
                || entityType.IsOwned()
                || entityType.BaseType is not null)
            {
                continue;
            }

            ApplyTenantFilterMethod
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped =>
        // Comparing against a nullable means an unset tenant produces `tenant_id = NULL`,
        // which is never true — so a unit of work that never identified a tenant reads
        // nothing rather than everything, the same way the database policies behave.
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == CurrentTenantId);
}
