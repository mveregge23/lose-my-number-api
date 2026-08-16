// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>How an attempt to add an address ended.</summary>
public enum AddAddressOutcome
{
    Added,

    /// <summary>No such profile for this tenant.</summary>
    ProfileNotFound,

    /// <summary>
    /// The profile already holds <see cref="ProfileLimits.MaxAddresses"/> of them.
    /// </summary>
    /// <remarks>
    /// Checked here rather than at the edge because it is the one rule that needs to
    /// know what is already stored, and what is already stored is encrypted.
    /// </remarks>
    TooMany,
}

/// <param name="Address">The address as stored, with the id assigned to it.</param>
public sealed record AddAddressResult(AddAddressOutcome Outcome, ProfileAddress? Address)
{
    public static AddAddressResult Failed(AddAddressOutcome outcome) => new(outcome, null);
}
