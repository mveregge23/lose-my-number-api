// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

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
public class DbrDbContext(DbContextOptions<DbrDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DbrDbContext).Assembly);
    }
}
