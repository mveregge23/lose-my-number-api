// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>
/// How much a profile is allowed to hold.
/// </summary>
/// <remarks>
/// <para>
/// Ordinary tables get these limits from the schema — a <c>varchar(200)</c>, a foreign
/// key, a unique index. A profile's identifying fields are one encrypted column each, so
/// the database has nothing to check: it cannot see a name, let alone how long it is.
/// These constants are the whole of the ceiling.
/// </para>
/// <para>
/// The numbers are set where a real person is comfortably under them and an automated
/// filler is not. They matter more than a length limit usually does, because every edit
/// re-encrypts every field: a profile that grew without bound would make each subsequent
/// change more expensive, and the growth would be invisible in the table.
/// </para>
/// </remarks>
public static class ProfileLimits
{
    /// <summary>
    /// Maiden names, married names, anglicisations, the spelling a broker guessed at.
    /// </summary>
    public const int MaxNames = 10;

    public const int MaxNameLength = 200;

    /// <summary>
    /// Current and historical both — brokers key off addresses somebody left years ago,
    /// which is often the only reason a listing is findable.
    /// </summary>
    public const int MaxAddresses = 20;

    public const int MaxAddressLineLength = 200;

    public const int MaxCityLength = 120;

    public const int MaxRegionLength = 120;

    public const int MaxPostalCodeLength = 20;

    public const int MaxContacts = 20;

    /// <summary>Long enough for the longest address a mail server will accept.</summary>
    public const int MaxContactValueLength = 320;

    /// <summary>
    /// A date before this is a typo or a joke rather than a person a broker has listed.
    /// </summary>
    public static DateOnly EarliestDateOfBirth { get; } = new(1900, 1, 1);
}
