// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Marks an <see cref="IEntityTypeConfiguration{TEntity}"/> as belonging to the vault
/// store rather than the core one.
/// </summary>
/// <remarks>
/// Both contexts discover their mappings by scanning this assembly, and a scan finds
/// everything — without a way to tell the two sets apart, each context would map the
/// other's entities and a join across the boundary would be one <c>Include</c> away.
/// A marker interface rather than a namespace check because it is a claim the
/// configuration makes about itself, visible where somebody writing a new one will see
/// it, and it cannot be defeated by moving a file.
/// </remarks>
public interface IVaultEntityConfiguration;
