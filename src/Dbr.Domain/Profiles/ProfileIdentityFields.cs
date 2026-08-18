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
/// <param name="Region">
/// State, province or equivalent. Nullable because plenty of addresses have none, and an
/// empty string standing in for that would be a value a later matcher has to know to
/// ignore.
/// </param>
/// <param name="PostalCode">Nullable for the same reason: not every country issues one.</param>
/// <param name="Country">Two-letter code, upper-cased, so it compares equal to catalog data.</param>
public sealed record ProfileAddress(
    Guid Id,
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string? PostalCode,
    string Country)
{
    /// <inheritdoc cref="ProfileIdentityFields.ToString"/>
    public override string ToString() => $"ProfileAddress {{ Id = {Id}, [withheld] }}";
}

/// <summary>How to reach someone, as a broker would have it.</summary>
public enum ProfileContactKind
{
    Email,
    Phone,
}

/// <summary>One contact point on a profile.</summary>
public sealed record ProfileContact(Guid Id, ProfileContactKind Kind, string Value)
{
    /// <inheritdoc cref="ProfileIdentityFields.ToString"/>
    public override string ToString() => $"ProfileContact {{ Id = {Id}, Kind = {Kind}, [withheld] }}";
}

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

    /// <summary>
    /// Names the type and withholds the contents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record's generated <c>ToString</c> prints every member it has, which makes any
    /// of these one string interpolation away from being somebody's identity in plain
    /// text — in a log line, an exception message, or a queue message. Overriding it
    /// puts the refusal on the type, where nothing has to remember to apply it.
    /// </para>
    /// <para>
    /// The redaction in the logging pipeline is the same rule enforced a layer out, and
    /// it is not a substitute for this one: it only sees values that reached a log event
    /// as properties, and a string built before the call arrives already spent.
    /// </para>
    /// </remarks>
    public override string ToString() =>
        $"ProfileIdentityFields {{ Names = {Names.Count}, Addresses = {Addresses.Count}, "
        + $"Contacts = {Contacts.Count}, [withheld] }}";
}

/// <summary>
/// Everything about an identity except its addresses.
/// </summary>
/// <remarks>
/// The grouping is the one the API replaces in a single request. Addresses are left out
/// because they are edited one at a time — a client adding an address it just learned
/// about should not have to resend a name to do it, and an old address is often the only
/// reason a listing is findable at all, so quietly dropping one because a request omitted
/// it would be the expensive kind of mistake.
/// </remarks>
public sealed record ProfileDetails(
    IReadOnlyList<string> Names,
    DateOnly? DateOfBirth,
    IReadOnlyList<ProfileContact> Contacts)
{
    /// <inheritdoc cref="ProfileIdentityFields.ToString"/>
    public override string ToString() =>
        $"ProfileDetails {{ Names = {Names.Count}, Contacts = {Contacts.Count}, [withheld] }}";
}
