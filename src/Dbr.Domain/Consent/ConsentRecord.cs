// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Consent;

/// <summary>
/// One decision a tenant made about one permission, at one moment, under one version of
/// the consent text.
/// </summary>
/// <remarks>
/// <para>
/// Not the current state of a permission — a decision about it. Granting and later
/// revoking leaves two rows, and the earlier one is not corrected or removed. The
/// newest row for a scope is what is in force now; the rest are what was in force
/// before.
/// </para>
/// <para>
/// That is the whole point of storing it this way. A boolean column would answer "may
/// this run", which is the easy question. The one that gets asked months later — when
/// somebody wants to know why a request went out in their name — is whether it was
/// permitted <i>then</i>, and under what wording. Overwriting the row makes that
/// unanswerable, and makes it unanswerable silently.
/// </para>
/// </remarks>
public class ConsentRecord : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account this decision belongs to.</summary>
    /// <remarks>
    /// Consent is held by the account, not by each identity it manages. Adding a second
    /// identity already takes its own explicit attestation, and asking again per profile
    /// for the same three permissions would be friction bought with nothing.
    /// </remarks>
    public Guid TenantId { get; init; }

    public required ConsentScope Scope { get; init; }

    /// <summary>Whether this decision permitted the scope or withdrew it.</summary>
    public required bool Granted { get; init; }

    /// <summary>When the tenant made this decision, and the key the newest row wins by.</summary>
    public DateTimeOffset EffectiveAt { get; init; }

    /// <summary>Which consent text was on screen when they made it.</summary>
    public required string PolicyVersion { get; init; }
}
