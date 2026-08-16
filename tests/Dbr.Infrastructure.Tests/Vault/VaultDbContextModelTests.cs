// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Dbr.Infrastructure.Tests.Persistence;
using Dbr.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Tests.Vault;

/// <summary>
/// That the two stores really are two, as far as the models are concerned.
/// </summary>
/// <remarks>
/// The database enforces the same separation with two roles, and that is the enforcing
/// half. This is the half that decides whether a query spanning the boundary can even be
/// written: an entity present in both models is one <c>Include</c> away from a join
/// nobody intended, and the day the vault moves to its own database that join stops
/// working with no warning beforehand.
/// </remarks>
public class VaultDbContextModelTests
{
    [Fact]
    public void Every_table_is_named_after_its_entity_type()
    {
        using var context = BuildVault();

        var mismatches = ModelConventions.TableNameMismatches(context);

        Assert.True(
            mismatches.Count == 0,
            "Every table must be the snake_cased name of its entity type, to match the "
            + "hand-written migrations in /db/migrations/vault/. Offenders: "
            + string.Join(", ", mismatches));
    }

    [Fact]
    public void Vault_entities_are_mapped_into_the_vault_schema()
    {
        using var context = BuildVault();

        var stray = context.Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() is not null)
            .Where(entityType => entityType.GetSchema() != VaultSchema.Name)
            .Select(entityType => entityType.ClrType.Name)
            .ToList();

        Assert.True(
            stray.Count == 0,
            "These vault entities map to the default schema, which is the core store's. "
            + $"Call ToTable(name, VaultSchema.Name): {string.Join(", ", stray)}");
    }

    [Fact]
    public void The_core_store_cannot_see_the_vault_entities()
    {
        using var core = BuildCore();

        Assert.DoesNotContain(
            core.Model.GetEntityTypes(),
            entityType => entityType.ClrType == typeof(ProfileIdentity));
    }

    [Fact]
    public void The_vault_store_cannot_see_the_core_entities()
    {
        using var core = BuildCore();
        using var vault = BuildVault();

        var coreTypes = core.Model.GetEntityTypes().Select(entityType => entityType.ClrType).ToHashSet();
        var shared = vault.Model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Where(coreTypes.Contains)
            .Select(type => type.Name)
            .ToList();

        Assert.True(
            shared.Count == 0,
            "These entities are mapped by both contexts, so a query can join across the "
            + "boundary the two stores exist to keep apart. A configuration belongs to one "
            + "store: mark the vault's with IVaultEntityConfiguration and leave the core's "
            + $"unmarked. Shared: {string.Join(", ", shared)}");
    }

    private static VaultDbContext BuildVault() =>
        new(
            new DbContextOptionsBuilder<VaultDbContext>()
                .UseDbr("Host=localhost;Database=dbr;Username=dbr;Password=x")
                .Options,
            new TenantContext());

    private static DbrDbContext BuildCore() =>
        new(
            new DbContextOptionsBuilder<DbrDbContext>()
                .UseDbr("Host=localhost;Database=dbr;Username=dbr;Password=x")
                .Options,
            new TenantContext());
}
