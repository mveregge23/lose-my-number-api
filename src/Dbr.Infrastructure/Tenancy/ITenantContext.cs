// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Tenancy;

/// <summary>
/// Which tenant the current unit of work is acting for.
/// </summary>
/// <remarks>
/// Scoped to one API request or one consumed message. This will be populated from a
/// validated JWT claim once authentication exists; until then nothing sets it, which
/// is the safe direction to be incomplete in — an unset tenant reaches the database
/// as "no tenant", and the row-level security policies match zero rows.
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// The tenant, or <see langword="null"/> when the work isn't on behalf of one —
    /// an unauthenticated request, a startup task, a background sweep over shared
    /// catalog data.
    /// </summary>
    Guid? TenantId { get; }
}
