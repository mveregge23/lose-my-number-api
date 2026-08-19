// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Dbr.Domain.Regions;

/// <summary>
/// The coarse region code — <c>US-CA</c>, <c>EU</c> — used both to say where somebody
/// lives and to say who a statute protects.
/// </summary>
/// <remarks>
/// <para>
/// Both sides of that comparison are here on purpose. Working out which regimes govern a
/// removal is a direct match between the region on a profile and the scope on a legal
/// basis, so a second spelling of the rule is not a tidiness problem: whichever side
/// drifted would stop matching, and the result would look like a jurisdiction with no
/// statute rather than like a bug.
/// </para>
/// <para>
/// Deliberately coarse. It is enough to resolve a deadline and not enough to identify
/// anybody, which is what lets it live outside the encrypted store and be read on every
/// request without a decryption.
/// </para>
/// </remarks>
public static partial class RegionCode
{
    /// <summary>
    /// The value as it is stored and compared, or <see langword="null"/> if nothing was
    /// given.
    /// </summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>Whether a normalized value is the right shape to be a region at all.</summary>
    public static bool IsWellFormed(string? value) =>
        value is not null && Pattern().IsMatch(value);

    /// <summary>
    /// Matches the check constraints on <c>privacy_profile.residency_region</c> and
    /// <c>legal_basis.residency_scope</c>, which are the same constraint written twice.
    /// </summary>
    [GeneratedRegex("^[A-Z]{2}(-[A-Z0-9]{1,3})?$")]
    private static partial Regex Pattern();
}
