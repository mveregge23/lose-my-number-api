// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// The vault store: envelope-encrypted identifying data, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A second context rather than a second schema on the first one. The separation is
/// meant to be real — its own connection string, its own migration journal, its own
/// database role, and eventually its own database — and a context that could reach both
/// stores would quietly make the last of those impossible, since the day the vault moves
/// every query spanning the two would break at once.
/// </para>
/// <para>
/// Only the profile service resolves this. Everything else asks the profile service,
/// which is what keeps the set of code holding plaintext small enough to read.
/// </para>
/// </remarks>
public class VaultDbContext(DbContextOptions<VaultDbContext> options, ITenantContext tenantContext)
    : TenantScopedDbContext(options, tenantContext)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(VaultDbContext).Assembly,
            configuration => typeof(IVaultEntityConfiguration).IsAssignableFrom(configuration));

        ApplyTenantQueryFilters(modelBuilder);
    }
}
