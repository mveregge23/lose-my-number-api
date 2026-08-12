// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.Domain.Tenancy;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// The core (non-vault) store: jobs, statuses, catalog, audit — everything outside
/// the envelope-encrypted store that holds personally identifying data.
/// </summary>
/// <remarks>
/// <para>
/// This is a runtime O/RM only; it does not own the schema. Tables, indexes,
/// extensions and the row-level security policies are created by hand-written SQL
/// under /db/migrations/core/ and applied by Dbr.Migrator.
/// </para>
/// <para>
/// There are no <c>DbSet</c>s yet. Entities arrive with the features that introduce
/// them, each shipping its own <see cref="IEntityTypeConfiguration{TEntity}"/>
/// alongside it, which <see cref="OnModelCreating"/> discovers automatically. Keeping
/// the mapping next to the entity is what lets the tenant boundary apply to every
/// table by convention instead of by a list somebody has to remember to update.
/// </para>
/// <para>
/// <b>Table naming:</b> a table is named after its entity type, snake_cased —
/// <c>PrivacyProfile</c> maps to <c>privacy_profile</c>. EF derives that default from
/// the <c>DbSet</c> property name when one exists, so a <c>DbSet</c> here is either
/// omitted (use <c>Set&lt;T&gt;()</c>) or named exactly after its entity type. A
/// pluralized <c>DbSet</c> would silently point the entity at a <c>tenants</c> table
/// while the migration created <c>tenant</c>, and nothing about that is visible at
/// the call site. <c>DbrDbContextModelTests</c> fails the build if one appears.
/// </para>
/// </remarks>
public class DbrDbContext(DbContextOptions options, ITenantContext tenantContext) : DbContext(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(DbrDbContext).GetMethod(
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DbrDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Narrows every <see cref="ITenantScoped"/> entity to the current tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <em>not</em> what keeps tenants apart — the row-level security policies
    /// are, and they hold whether this runs or not. This exists because the policies
    /// are enforced somewhere the application cannot see, by configuration the
    /// application does not control. A revoked <c>FORCE</c>, a connection that reached
    /// the database as the wrong role, a table whose migration forgot to call
    /// <c>app.enable_tenant_rls</c> — each of those turns the database into something
    /// that hands back every tenant's rows, and this makes the application ask for
    /// only its own anyway.
    /// </para>
    /// <para>
    /// Applied by convention rather than per entity, because the failure mode of
    /// listing them by hand is forgetting one, and the symptom of forgetting one is a
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
