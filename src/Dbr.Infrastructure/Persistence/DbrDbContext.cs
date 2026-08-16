// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Tenancy;
using Dbr.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// The core (non-vault) store: jobs, statuses, catalog, audit — everything outside the
/// envelope-encrypted store that holds personally identifying data.
/// </summary>
/// <remarks>
/// <para>
/// This is a runtime O/RM only; it does not own the schema. Tables, indexes, extensions
/// and the row-level security policies are created by hand-written SQL under
/// /db/migrations/core/ and applied by Dbr.Migrator.
/// </para>
/// <para>
/// Entities arrive with the features that introduce them, each shipping its own
/// <see cref="IEntityTypeConfiguration{TEntity}"/> alongside it, which
/// <see cref="OnModelCreating"/> discovers automatically. Keeping the mapping next to
/// the entity is what lets the tenant boundary apply to every table by convention
/// instead of by a list somebody has to remember to update.
/// </para>
/// <para>
/// <b>What it deliberately cannot see.</b> The vault store's entities are configured by
/// <see cref="VaultDbContext"/> and are excluded here, so this model has no way to
/// express a query touching them — a join from an account to the names and addresses
/// behind it does not compile, let alone run. The connection this context opens is
/// refused those tables by the database as well; the two halves say the same thing, one
/// at build time and one at run time.
/// </para>
/// <para>
/// <b>Table naming:</b> a table is named after its entity type, snake_cased —
/// <c>PrivacyProfile</c> maps to <c>privacy_profile</c>. EF derives that default from
/// the <c>DbSet</c> property name when one exists, so a <c>DbSet</c> here is either
/// omitted (use <c>Set&lt;T&gt;()</c>) or named exactly after its entity type. A
/// pluralized <c>DbSet</c> would silently point the entity at a <c>tenants</c> table
/// while the migration created <c>tenant</c>, and nothing about that is visible at the
/// call site. <c>DbrDbContextModelTests</c> fails the build if one appears.
/// </para>
/// </remarks>
/// <remarks>
/// The options parameter is typed to this context rather than the base
/// <c>DbContextOptions</c>: with a second context registered, EF refuses to construct
/// either one through an untyped parameter, since it can no longer tell whose options it
/// is being handed. It worked while there was only one, which is exactly the kind of
/// thing that breaks on the day something is added rather than the day it is written.
/// </remarks>
public class DbrDbContext(DbContextOptions<DbrDbContext> options, ITenantContext tenantContext)
    : TenantScopedDbContext(options, tenantContext)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(DbrDbContext).Assembly,
            configuration => !typeof(IVaultEntityConfiguration).IsAssignableFrom(configuration));

        ApplyTenantQueryFilters(modelBuilder);

        // The tenant itself cannot go through the convention above: it has no TenantId
        // to compare, because the tenant this row belongs to is the one it is. Its
        // table's row-level security policy is created over the same column, so the two
        // halves agree.
        modelBuilder.Entity<Tenant>().HasQueryFilter(tenant => tenant.Id == CurrentTenantId);
    }
}
