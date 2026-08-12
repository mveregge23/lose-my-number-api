// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Tenancy;

/// <summary>
/// Which tenant the current unit of work is acting for.
/// </summary>
/// <remarks>
/// Scoped: an API request or a consumed message. §4 says this comes from a validated
/// JWT claim, which is DBR-011's job — until then nothing populates it, and that is
/// the safe direction to be incomplete in. An unset tenant reaches the database as
/// "no tenant", and the policies match zero rows.
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
