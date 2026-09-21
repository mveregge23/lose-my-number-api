// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Search;

namespace Dbr.Domain.Profiles;

/// <summary>
/// The one spelling of each identity group: what a column holds, and what a grant
/// records having handed over.
/// </summary>
/// <remarks>
/// <para>
/// The same arrangement the catalog and monitoring vocabularies describe, and here for
/// the same reason: each of these strings appears in a check constraint and in the value
/// read back out of the column, and two spellings of one group would let a grant record a
/// field nothing can match on.
/// </para>
/// <para>
/// <b>These are not the strings the cipher binds to.</b> That one interpolates the enum
/// member name, so it is <c>DateOfBirth</c> where this is <c>date_of_birth</c>, and the
/// two must not be conflated: changing this spelling is a migration widening a check
/// constraint, while changing that one makes every existing ciphertext unreadable. They
/// are deliberately different-looking so that a search-and-replace cannot quietly do the
/// second while meaning the first.
/// </para>
/// </remarks>
public static class IdentityVocabulary
{
    public static string ToWire(IdentityField field) => field switch
    {
        IdentityField.Names => "names",
        IdentityField.Addresses => "addresses",
        IdentityField.Contacts => "contacts",
        IdentityField.DateOfBirth => "date_of_birth",
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field,
            "Unmapped identity field. Adding one means a migration widening the check "
            + "constraint on identity_release.fields as well."),
    };

    public static IdentityField? Parse(string? value) => value switch
    {
        "names" => IdentityField.Names,
        "addresses" => IdentityField.Addresses,
        "contacts" => IdentityField.Contacts,
        "date_of_birth" => IdentityField.DateOfBirth,
        _ => null,
    };

    /// <summary>
    /// How closely a listing agreed with a group, spelled once for the edge, the row and
    /// the API. The same rule as the field names: one spelling, or the three drift.
    /// </summary>
    public static string ToWire(MatchStrength strength) => strength switch
    {
        MatchStrength.Exact => "exact",
        MatchStrength.Partial => "partial",
        MatchStrength.Conflicting => "conflicting",
        _ => throw new ArgumentOutOfRangeException(
            nameof(strength),
            strength,
            "Unmapped match strength. Adding one means a migration widening the check "
            + "constraints on the exposure's agreement columns as well."),
    };

    public static MatchStrength? ParseStrength(string? value) => value switch
    {
        "exact" => MatchStrength.Exact,
        "partial" => MatchStrength.Partial,
        "conflicting" => MatchStrength.Conflicting,
        _ => null,
    };
}
