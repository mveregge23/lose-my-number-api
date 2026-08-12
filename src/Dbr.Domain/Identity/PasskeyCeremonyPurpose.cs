// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Identity;

/// <summary>
/// What a <see cref="PasskeyCeremony"/> was issued for.
/// </summary>
/// <remarks>
/// Checked when a ceremony is claimed, not merely recorded. The two kinds carry
/// different options and are verified by different code, so a registration ceremony
/// completed as a login — or the reverse — is a confusion worth refusing outright
/// rather than discovering partway through a verification that was never meant to run
/// against it.
/// <para>
/// Stored lower-cased as text, with a check constraint listing the permitted values,
/// for the same reason account status is: adding a value to a Postgres enum cannot be
/// undone.
/// </para>
/// </remarks>
public enum PasskeyCeremonyPurpose
{
    /// <summary>Creating a passkey — for a new account, or an existing one.</summary>
    Registration,

    /// <summary>Proving possession of a passkey that already exists.</summary>
    Authentication,
}
