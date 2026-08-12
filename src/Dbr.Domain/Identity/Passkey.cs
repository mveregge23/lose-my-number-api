// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Identity;

/// <summary>
/// A passkey: the public half of a WebAuthn credential, and one of the ways an
/// account proves it is being operated by its owner.
/// </summary>
/// <remarks>
/// There is nothing secret in here. The private key never leaves the authenticator
/// that generated it, so an attacker holding this row holds a public key they cannot
/// sign with and a counter they cannot usefully change. That is the whole reason
/// passkeys are the primary credential: a stolen copy of this table is not a stolen
/// set of logins, which is not true of anything derived from a password.
/// </remarks>
public class Passkey : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account this passkey signs in.</summary>
    public Guid TenantId { get; init; }

    /// <summary>
    /// The authenticator's own handle for this credential, and the only thing a login
    /// attempt arrives holding — which is why it is unique across every account
    /// rather than within one.
    /// </summary>
    public required byte[] CredentialId { get; init; }

    /// <summary>COSE-encoded, exactly as the authenticator produced it.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>
    /// The authenticator's use counter as of the last accepted assertion.
    /// </summary>
    /// <remarks>
    /// Written back on every successful login, because the value is only useful in
    /// comparison with the previous one: a counter that fails to advance is how a
    /// cloned authenticator gives itself away. Authenticators that keep no counter
    /// report zero forever, which is permitted, and for those this proves nothing.
    /// </remarks>
    public long SignatureCount { get; set; }

    /// <summary>
    /// Whether the authenticator says this credential is allowed to be copied off the
    /// device it was created on.
    /// </summary>
    public bool IsBackupEligible { get; init; }

    /// <summary>
    /// Whether it actually has been. Together with <see cref="IsBackupEligible"/>
    /// this is what separates a passkey synced to a password manager from one that
    /// lives on a single device and is lost with it — which decides whether a lost
    /// phone is an inconvenience or a lost account.
    /// </summary>
    /// <remarks>
    /// Settable because it changes without this credential being re-registered: a
    /// passkey created before its owner turned on syncing reports the change on a
    /// later assertion.
    /// </remarks>
    public bool IsBackedUp { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When this passkey last signed in, or <see langword="null"/> if it never has.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
