// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Identity;

/// <summary>
/// A WebAuthn challenge that has been issued and is waiting to be answered.
/// </summary>
/// <remarks>
/// <para>
/// Registering or using a passkey takes two round trips, and the middle of it is
/// server state. It has to be: the guarantee an assertion gives is that the
/// authenticator signed <em>this server's</em> challenge just now, and a challenge the
/// client keeps and hands back to itself carries no such guarantee — whoever chooses
/// the challenge can choose one an old signature already covers.
/// </para>
/// <para>
/// Deliberately not tenant-scoped, unlike almost everything else. A ceremony exists
/// precisely during the window where there is no tenant: a login ceremony is issued to
/// someone who has not said who they are, and a registration ceremony is issued before
/// the account exists at all. What stands in for the boundary is
/// <see cref="Id"/> — random, unguessable, and known only to whoever it was handed to.
/// </para>
/// </remarks>
public class PasskeyCeremony
{
    /// <summary>
    /// Handed to the client and quoted back to finish. This is the only thing naming
    /// the row, so it is the only thing keeping one caller from completing another's
    /// ceremony.
    /// </summary>
    public Guid Id { get; init; }

    public PasskeyCeremonyPurpose Purpose { get; init; }

    /// <summary>
    /// The options that were issued, verbatim, including the challenge.
    /// </summary>
    /// <remarks>
    /// Stored whole rather than rebuilt at verification time. An equivalent-looking
    /// object assembled twice can drift, and drift here would show up as an
    /// authentication passing against options nobody ever sent.
    /// </remarks>
    public required string Options { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>After this, the ceremony is refused whether or not it was used.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Set when the second leg claims this ceremony, which is what makes it
    /// single-use.
    /// </summary>
    /// <remarks>
    /// The row is kept rather than deleted so a replay is a claim against a ceremony
    /// that is already spent — a different thing from one that expired, and from one
    /// that never existed.
    /// </remarks>
    public DateTimeOffset? ConsumedAt { get; set; }
}
