// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>A postal address a broker might have on file.</summary>
/// <remarks>
/// Current and former both, because brokers key off addresses somebody moved away from
/// years ago — an old address is often the thing that makes a listing findable at all.
/// The id is generated here rather than by a database, since these live encrypted
/// inside a single column and there is nothing for a sequence to number; it is what
/// lets one address be removed later without the caller re-sending the rest.
/// </remarks>
public sealed record ProfileAddress(
    Guid Id,
    string Line1,
    string? Line2,
    string City,
    string Region,
    string PostalCode,
    string Country);

/// <summary>How to reach someone, as a broker would have it.</summary>
public enum ProfileContactKind
{
    Email,
    Phone,
}

/// <summary>One contact point on a profile.</summary>
public sealed record ProfileContact(Guid Id, ProfileContactKind Kind, string Value);

/// <summary>
/// Everything identifying about one profile, in the clear.
/// </summary>
/// <remarks>
/// <para>
/// This type only exists inside the profile service and whatever called it. It is
/// never a database row, never a queue message, and never a log entry — the storage
/// shape is four ciphertext columns, and the release shape is whichever single field a
/// job asked for.
/// </para>
/// <para>
/// The groupings are not cosmetic: names, addresses, contacts and date of birth are
/// encrypted separately so that releasing a name to a worker does not require
/// decrypting a date of birth.
/// </para>
/// </remarks>
public sealed record ProfileIdentityFields(
    IReadOnlyList<string> Names,
    IReadOnlyList<ProfileAddress> Addresses,
    IReadOnlyList<ProfileContact> Contacts,
    DateOnly? DateOfBirth)
{
    /// <summary>A profile that has been created but not filled in yet.</summary>
    public static ProfileIdentityFields Empty { get; } = new([], [], [], null);
}
