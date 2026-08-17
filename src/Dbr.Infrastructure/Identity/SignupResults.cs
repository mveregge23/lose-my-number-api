// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Identity;

/// <summary>How an attempt to open an account ended.</summary>
/// <remarks>
/// Its own enum rather than the passkey one it mostly mirrors, because opening an
/// account can fail for a reason the ceremony knows nothing about — the terms moved
/// while somebody was reading them. Mapping the credential outcomes through here is what
/// keeps that from being bolted onto a type describing what an authenticator said.
/// </remarks>
public enum SignupOutcome
{
    /// <summary>The account, its first passkey, and its own profile all exist.</summary>
    Created,

    /// <summary>
    /// The ceremony was unknown, expired, already finished, or issued for something
    /// else.
    /// </summary>
    CeremonyUnusable,

    /// <summary>The authenticator's answer did not check out.</summary>
    AttestationRejected,

    /// <summary>The address already has an account.</summary>
    AddressAlreadyRegistered,

    /// <summary>
    /// The accepted version is not the one this instance is serving.
    /// </summary>
    /// <remarks>
    /// Checked before the ceremony is spent, so the client can show the current text
    /// and finish the same ceremony. The alternative — recording whatever version the
    /// client claimed — would make the attestation a record of the client's word rather
    /// than of what the account holder was shown.
    /// </remarks>
    TermsOutOfDate,
}

/// <param name="TenantId">
/// The account created, or <see cref="Guid.Empty"/> when none was.
/// </param>
public sealed record SignupResult(SignupOutcome Outcome, Guid TenantId)
{
    public static SignupResult Failed(SignupOutcome outcome) => new(outcome, Guid.Empty);
}
