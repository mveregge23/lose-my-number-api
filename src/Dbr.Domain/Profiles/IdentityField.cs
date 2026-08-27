// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>
/// One of the four groups an identity is stored in, and therefore one of the four a job
/// can be given without being given the rest.
/// </summary>
/// <remarks>
/// <para>
/// The members are groups rather than individual values because that is the granularity
/// the vault encrypts at: names, addresses, contacts and date of birth are four separate
/// ciphertexts, so handing a worker a name does not require decrypting a date of birth. A
/// finer vocabulary here would promise a scoping the storage cannot actually perform, and
/// a coarser one would make every release all-or-nothing.
/// </para>
/// <para>
/// It lives beside the profile rather than beside the first thing that needed it, because
/// anything asking for part of an identity has to name the part it wants. Searching a
/// broker is only the first of those; submitting a removal will name fields from the same
/// list, and two lists would drift apart on the day somebody adds a fifth group to one of
/// them.
/// </para>
/// </remarks>
public enum IdentityField
{
    /// <summary>Every spelling of the name that is on file.</summary>
    Names,

    /// <summary>Postal addresses, current and former both.</summary>
    Addresses,

    /// <summary>Email addresses and phone numbers.</summary>
    Contacts,

    /// <summary>The date of birth, for a profile that carries one.</summary>
    DateOfBirth,
}
