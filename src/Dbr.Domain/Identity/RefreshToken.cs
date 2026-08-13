// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Identity;

/// <summary>
/// The revocable half of a session.
/// </summary>
/// <remarks>
/// <para>
/// An access token is checked by verifying a signature, which means it is checked
/// without asking anything — fast, and impossible to take back. This is the part that
/// can be taken back. Signing out, expiring a session, and dealing with a stolen
/// token are all operations on these rows.
/// </para>
/// <para>
/// The token itself is not here and never was: what is stored is its digest, so
/// reading this table yields nothing anyone can present.
/// </para>
/// </remarks>
public class RefreshToken : ITenantScoped
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    /// <summary>
    /// SHA-256 of the token. The token is 256 bits from a CSPRNG, so there is nothing
    /// for an attacker to guess and no reason to pay for a slow hash on every refresh.
    /// </summary>
    public required byte[] TokenHash { get; init; }

    /// <summary>
    /// Shared by every token descended from one sign-in.
    /// </summary>
    /// <remarks>
    /// Rotation replaces a token and keeps the session, which is what makes "sign this
    /// session out" and "everything descended from this sign-in is compromised" the
    /// same kind of operation.
    /// </remarks>
    public Guid SessionId { get; init; }

    /// <summary>
    /// When the sign-in behind this session happened, carried forward unchanged
    /// across rotations.
    /// </summary>
    /// <remarks>
    /// Not refreshed by rotation, deliberately. If each rotation moved this, a session
    /// that keeps being used would never end — including one being kept alive by
    /// somebody who stole it.
    /// </remarks>
    public DateTimeOffset SessionStartedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// When this token was exchanged for its successor, if it has been.
    /// </summary>
    /// <remarks>
    /// The row survives being spent so that presenting it again is recognisable. A
    /// deleted row would make a stolen token indistinguishable from one that never
    /// existed, and those two deserve very different answers.
    /// </remarks>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>
    /// When the token was deliberately invalidated — a sign-out, or the whole session
    /// being torn down after a spent token reappeared.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
