// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// The core (non-vault) store: jobs, statuses, catalog, audit — everything outside
/// the envelope-encrypted vault described in §4. Runtime O/RM only; it does not own
/// the schema. Tables, indexes, extensions and the RLS policies are created by the
/// hand-written SQL under /db/migrations/core/ and applied by Dbr.Migrator (§18).
/// </summary>
/// <remarks>
/// <para>
/// There are no <c>DbSet</c>s yet on purpose. Entities arrive with the stories that
/// introduce them (DBR-008 onward), each shipping its own
/// <see cref="IEntityTypeConfiguration{TEntity}"/> alongside it, which
/// <see cref="OnModelCreating"/> discovers automatically. Keeping the mapping next
/// to the entity is what lets the tenant-boundary work in DBR-004/005 apply to every
/// table by convention instead of by a list somebody has to remember to update.
/// </para>
/// <para>
/// <b>Table naming:</b> a table is named after its entity type, snake_cased —
/// <c>PrivacyProfile</c> maps to <c>privacy_profile</c>, matching the singular names
/// §18.4 gives its own migration scripts. EF derives that default from the
/// <c>DbSet</c> property name when one exists, so a <c>DbSet</c> here is either
/// omitted (use <c>Set&lt;T&gt;()</c>) or named exactly after its entity type. A
/// pluralized <c>DbSet</c> would silently retarget the table and disagree with the
/// migration that created it; <c>DbrDbContextModelTests</c> fails the build if one
/// appears, and the §18.6 schema-drift test (DBR-069) catches it against a real
/// database as the second net.
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
