// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Tenancy;

/// <summary>
/// Marks an entity as belonging to exactly one tenant.
/// </summary>
/// <remarks>
/// <para>
/// Implementing this is how an entity opts into the query filter that keeps one
/// tenant's rows out of another's results. It is the mirror of the database policy
/// its table gets from <c>app.enable_tenant_rls</c>, and the two are expected to
/// travel together: the interface on the entity, the procedure call in its migration.
/// </para>
/// <para>
/// Not every table has a tenant. Broker catalog entries and the pacing state shared
/// across tenants for a given broker deliberately sit outside this, and must not
/// implement it — a filter comparing a column that does not exist fails at query
/// time.
/// </para>
/// </remarks>
public interface ITenantScoped
{
    /// <summary>
    /// The owning tenant. Set when the row is created and never changed afterwards —
    /// the database rejects a write that would move a row between tenants.
    /// </summary>
    Guid TenantId { get; }
}
