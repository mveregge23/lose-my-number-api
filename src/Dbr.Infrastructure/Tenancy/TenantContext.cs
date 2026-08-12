// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Tenancy;

/// <summary>
/// The scoped, write-once holder of the current tenant.
/// </summary>
/// <remarks>
/// Write-once rather than a settable property: within one request or one message the
/// tenant is established once, by whoever validated the credential, and a later
/// reassignment would mean a unit of work that acted for two tenants. Since a
/// connection may already have been opened and pinned to the first one, that would
/// silently produce a mixed-tenant transaction — so it throws instead.
/// </remarks>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    /// <exception cref="InvalidOperationException">A different tenant is already set.</exception>
    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Guid.Empty is not a tenant. Leave the context unset to act for no tenant.",
                nameof(tenantId));
        }

        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException(
                $"This scope is already acting for tenant {existing} and cannot switch to "
                + $"{tenantId}. A connection may already be pinned to the first tenant, so "
                + "the result would be a unit of work spanning two of them. Start a new scope.");
        }

        TenantId = tenantId;
    }
}
