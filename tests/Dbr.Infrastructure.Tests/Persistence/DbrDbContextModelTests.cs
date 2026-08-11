// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Tests.Persistence;

/// <summary>
/// Guards the real model against the one naming mistake that is easy to make and
/// invisible once made.
/// </summary>
/// <remarks>
/// EF derives a table name from the <c>DbSet</c> property when there is one, so
/// adding <c>DbSet&lt;Tenant&gt; Tenants</c> silently retargets the entity at a
/// <c>tenants</c> table while the migration of §18.4 created <c>tenant</c>. Nothing
/// about that is visible at the call site. The §18.6 drift test (DBR-069) catches it
/// too, but only against a real database — this catches it in the unit tier, which
/// is where the mistake gets made.
/// <para>
/// Vacuous until the first entity lands in DBR-008: <see cref="DbrDbContext"/> has no
/// entities yet, and the fence is deliberately built before the field it encloses.
/// </para>
/// </remarks>
public partial class DbrDbContextModelTests
{
    [Fact]
    public void Every_table_is_named_after_its_entity_type()
    {
        using var context = new DbrDbContext(
            new DbContextOptionsBuilder<DbrDbContext>()
                .UseDbr("Host=localhost;Database=dbr;Username=dbr;Password=x")
                .Options);

        var mismatches = context.Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() is not null && !entityType.IsOwned())
            .Select(entityType => new
            {
                Entity = entityType.ClrType.Name,
                Actual = entityType.GetTableName(),
                Expected = ToSnakeCase(entityType.ClrType.Name),
            })
            .Where(mapping => mapping.Actual != mapping.Expected)
            .ToList();

        Assert.True(
            mismatches.Count == 0,
            "Every table must be the snake_cased name of its entity type, to match the "
            + "hand-written migrations in /db/migrations/core/. Offenders (usually a "
            + "pluralized DbSet property — name it after the entity type, or drop it and "
            + "use Set<T>()): "
            + string.Join(", ", mismatches.Select(m => $"{m.Entity} -> '{m.Actual}', expected '{m.Expected}'")));
    }

    /// <summary>
    /// Mirrors what the snake_case naming convention does, so the assertion states the
    /// expectation independently rather than asking the mapping to agree with itself.
    /// </summary>
    private static string ToSnakeCase(string name) =>
        WordBoundary().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
