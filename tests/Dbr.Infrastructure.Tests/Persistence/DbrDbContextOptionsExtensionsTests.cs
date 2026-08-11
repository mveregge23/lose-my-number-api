// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dbr.Infrastructure.Tests.Persistence;

/// <summary>
/// These build a model rather than a connection — nothing here touches a database.
/// The point is the mapping conventions: §18 puts the schema in hand-written SQL, so
/// the names EF expects have to match the names those scripts create, and that
/// agreement is worth a test that fails the moment the convention changes.
/// </summary>
public class DbrDbContextOptionsExtensionsTests
{
    private const string AnyConnectionString = "Host=localhost;Database=dbr;Username=dbr;Password=x";

    [Fact]
    public void UseDbr_maps_entity_names_to_singular_snake_case()
    {
        using var context = NamingProbeContext();

        var entityType = context.Model.FindEntityType(typeof(RemovalJobProbe))!;

        Assert.Equal("removal_job_probe", entityType.GetTableName());
    }

    [Fact]
    public void UseDbr_maps_property_names_to_snake_case()
    {
        using var context = NamingProbeContext();

        var entityType = context.Model.FindEntityType(typeof(RemovalJobProbe))!;
        var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;

        // The interesting case is a multi-word name: `TenantId` has to become
        // `tenant_id`, because that is what the RLS policies in §4 will reference.
        Assert.Equal(
            "tenant_id",
            entityType.FindProperty(nameof(RemovalJobProbe.TenantId))!.GetColumnName(storeObject));
    }

    [Fact]
    public void UseDbr_rejects_a_missing_connection_string()
    {
        var builder = new DbContextOptionsBuilder<NamingProbeDbContext>();

        Assert.Throws<ArgumentException>(() => builder.UseDbr("   "));
    }

    private static NamingProbeDbContext NamingProbeContext() =>
        new(new DbContextOptionsBuilder<NamingProbeDbContext>()
            .UseDbr(AnyConnectionString)
            .Options);

    /// <summary>
    /// A stand-in for the entities that arrive from DBR-008 onward. It lives in the
    /// test project on purpose: the real <see cref="DbrDbContext"/> has no entities
    /// yet, and this asserts the convention rather than any one entity.
    /// </summary>
    private sealed class NamingProbeDbContext(DbContextOptions<NamingProbeDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RemovalJobProbe>();
    }

    private sealed class RemovalJobProbe
    {
        public Guid Id { get; init; }

        public Guid TenantId { get; init; }
    }
}
