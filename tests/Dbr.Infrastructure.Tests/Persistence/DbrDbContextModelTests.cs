// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Tests.Persistence;

/// <summary>
/// Guards the real model against the one naming mistake that is easy to make and
/// invisible once made.
/// </summary>
/// <remarks>
/// EF derives a table name from the <c>DbSet</c> property when there is one, so adding
/// <c>DbSet&lt;Tenant&gt; Tenants</c> silently points the entity at a <c>tenants</c>
/// table while the migration created <c>tenant</c>. Nothing about that is visible at the
/// call site. The schema-drift test catches it too, but only against a real database —
/// this catches it in the unit tier, which is where the mistake gets made.
/// </remarks>
public class DbrDbContextModelTests
{
    [Fact]
    public void Every_table_is_named_after_its_entity_type()
    {
        using var context = new DbrDbContext(
            new DbContextOptionsBuilder<DbrDbContext>()
                .UseDbr("Host=localhost;Database=dbr;Username=dbr;Password=x")
                .Options,
            new TenantContext());

        var mismatches = ModelConventions.TableNameMismatches(context);

        Assert.True(
            mismatches.Count == 0,
            "Every table must be the snake_cased name of its entity type, to match the "
            + "hand-written migrations in /db/migrations/core/. Offenders (usually a "
            + "pluralized DbSet property — name it after the entity type, or drop it and "
            + "use Set<T>()): "
            + string.Join(", ", mismatches));
    }
}
