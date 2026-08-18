// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// How hard a regime lets a broker make somebody prove who they are before it acts.
/// </summary>
/// <remarks>
/// Not informational. A regime that permits enhanced verification is one where a
/// removal can stop and ask the person for something no automated step can supply, and
/// the connector contract already has a shape for that — this is what tells a
/// dispatcher to expect it rather than to treat the pause as a failure.
/// </remarks>
public enum VerificationLevel
{
    /// <summary>The request stands on its own.</summary>
    None,

    /// <summary>Something the request already carries is enough to match on.</summary>
    Basic,

    /// <summary>
    /// The broker may demand identity documents, which only the person can provide.
    /// </summary>
    Enhanced,
}
