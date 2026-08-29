// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// How long a grant stays redeemable.
/// </summary>
/// <remarks>
/// The one thing about a release an operator may reasonably tune, and the one worth
/// getting wrong in only one direction. Too short and a leg whose lane was busy finds its
/// grant expired and has to be re-planned; too long and a message sitting in a queue is a
/// standing decryption right for as long as the queue is deep. The default is the five
/// minutes the design names, which is generous for work that starts the moment its lane
/// allows.
/// </remarks>
public sealed class IdentityReleaseOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "IdentityRelease";

    /// <summary>
    /// The longest lifetime an operator may configure.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than advice. Past about an hour the grant stops being scoped to a
    /// piece of work in any meaningful sense, and an instance that wants standing access
    /// should have to say so by changing code rather than by moving a number.
    /// </remarks>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    /// <summary>How long after minting a grant may still be spent.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fails startup on a value no grant could work with, rather than at the moment one
    /// is minted.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (Lifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Lifetime is {Lifetime}, which mints grants that have already "
                + "expired. Every release would be refused, and it would look like a broken "
                + "worker rather than a setting.");
        }

        if (Lifetime > MaxLifetime)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Lifetime is {Lifetime}, longer than the {MaxLifetime} ceiling. "
                + "A grant that outlives the work it was minted for is standing access to an "
                + "identity, which is the thing scoping it was for.");
        }
    }
}
