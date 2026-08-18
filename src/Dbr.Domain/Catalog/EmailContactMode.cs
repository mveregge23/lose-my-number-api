// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Which address a broker will correspond about a removal at.
/// </summary>
/// <remarks>
/// The distinction costs somebody a disclosure, which is why it is catalog data rather
/// than a setting. A broker that accepts an alias can be contacted without handing over
/// anything it did not already have; one that insists on the address it holds on file
/// is asking the person to confirm it, and that is a thing to know before the request
/// is sent rather than after.
/// </remarks>
public enum EmailContactMode
{
    /// <summary>An alias is accepted, so nothing new is disclosed.</summary>
    AliasPreferred,

    /// <summary>The broker will only correspond at the address it already holds.</summary>
    TenantRealRequired,
}
