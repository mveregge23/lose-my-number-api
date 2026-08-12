// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Identity;

/// <summary>
/// Whether an account may act.
/// </summary>
/// <remarks>
/// Stored lower-cased as text, with a check constraint listing the permitted values.
/// Adding a value is therefore an ordinary migration rather than an ALTER TYPE, which
/// on a Postgres enum cannot be undone and historically could not run in a
/// transaction.
/// </remarks>
public enum TenantStatus
{
    /// <summary>Normal. The account can sign in and start work.</summary>
    Active,

    /// <summary>
    /// Blocked from acting, enforced before authentication rather than at each
    /// endpoint — so a new route cannot forget to check it. Existing data is
    /// untouched: suspension is not deletion.
    /// </summary>
    Suspended,
}
