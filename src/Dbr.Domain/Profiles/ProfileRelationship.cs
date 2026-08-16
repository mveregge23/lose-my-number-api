// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>
/// What a tenant claims this identity is to them.
/// </summary>
/// <remarks>
/// <para>
/// The distinction exists because nothing here verifies that a claim is true. Removing
/// your own public data is the common case and carries no added risk; putting somebody
/// else's name and address into a profile and running the same machinery against them
/// is a way to harass a person who never asked for any of this. Making the second case
/// say so — explicitly, against a small cap, with the attestation recorded — bounds
/// how much damage a false claim can do, which is the achievable version of preventing
/// it.
/// </para>
/// <para>
/// Stored as text with a check constraint listing the permitted values, so adding one
/// is an ordinary migration.
/// </para>
/// </remarks>
public enum ProfileRelationship
{
    /// <summary>
    /// The tenant's own identity. Created at signup, attested by the terms accepted
    /// there, and never separately deletable — closing the account is what removes it.
    /// </summary>
    Self,

    /// <summary>Someone the tenant is responsible for: a minor, an elderly relative.</summary>
    Dependent,

    /// <summary>
    /// Someone the tenant is authorized to act for by some other arrangement — a power
    /// of attorney, an estate executor.
    /// </summary>
    AuthorizedOther,
}
