// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>How a broker accepts an opt-out or deletion request.</summary>
/// <remarks>
/// This decides which connector a job runs, and it is a fact about the company rather
/// than a preference: a broker offering only a mailbox cannot be sent a form no matter
/// what would be more convenient.
/// </remarks>
public enum RemovalMethod
{
    /// <summary>A web form, driven by a browser.</summary>
    WebForm,

    /// <summary>An opt-out mailbox, and nothing else.</summary>
    Email,

    /// <summary>A documented API.</summary>
    Api,

    /// <summary>Paper. Rare, slow, and still occasionally the only option.</summary>
    Postal,
}
