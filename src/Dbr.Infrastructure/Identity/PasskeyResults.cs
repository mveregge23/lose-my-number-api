// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Identity;

/// <summary>A challenge that has been issued, and the handle to finish it with.</summary>
public sealed record PasskeyChallenge<TOptions>(Guid CeremonyId, TOptions Options);

/// <summary>How an attempt to open an account with a passkey ended.</summary>
public enum PasskeySignupOutcome
{
    /// <summary>The account exists and its first passkey is registered.</summary>
    Created,

    /// <summary>
    /// The ceremony was unknown, expired, already finished, or issued for something
    /// else. Which of those is deliberately not distinguished.
    /// </summary>
    CeremonyUnusable,

    /// <summary>The authenticator's answer did not check out.</summary>
    AttestationRejected,

    /// <summary>
    /// The address already has an account.
    /// </summary>
    /// <remarks>
    /// Only reachable after a complete, valid ceremony — an authenticator has to have
    /// signed this server's challenge before the address is ever compared. That is
    /// not free of the enumeration problem, but it does mean probing an address costs
    /// a real credential and a real ceremony rather than an HTTP request.
    /// </remarks>
    AddressAlreadyRegistered,
}

/// <param name="TenantId">
/// The account created, or <see cref="Guid.Empty"/> when none was.
/// </param>
public sealed record PasskeySignupResult(PasskeySignupOutcome Outcome, Guid TenantId)
{
    public static PasskeySignupResult Failed(PasskeySignupOutcome outcome) => new(outcome, Guid.Empty);
}

/// <summary>How an attempt to sign in with a passkey ended.</summary>
public enum PasskeyLoginOutcome
{
    /// <summary>The assertion verified, and the account is known.</summary>
    Authenticated,

    /// <summary>
    /// The ceremony was unknown, expired, already finished, or issued for something
    /// else.
    /// </summary>
    CeremonyUnusable,

    /// <summary>
    /// The assertion did not verify — a bad signature, a challenge that was not the
    /// one issued, an unknown credential, a counter that failed to advance.
    /// </summary>
    /// <remarks>
    /// One value rather than several on purpose. Telling a caller which of these it
    /// was tells them whether the credential they presented exists here, which is the
    /// question the whole login path is built to avoid answering.
    /// </remarks>
    AssertionRejected,
}

/// <param name="TenantId">
/// The account signed in, or <see cref="Guid.Empty"/> when none was.
/// </param>
public sealed record PasskeyLoginResult(PasskeyLoginOutcome Outcome, Guid TenantId)
{
    public static PasskeyLoginResult Failed(PasskeyLoginOutcome outcome) => new(outcome, Guid.Empty);
}
