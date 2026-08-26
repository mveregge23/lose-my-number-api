// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Removals;

/// <summary>How a removal is actually carried out.</summary>
/// <remarks>
/// Chosen by the broker's catalog entry rather than by the tenant. The dispatcher is
/// otherwise identical across all three, which is what makes adding a broker a catalog
/// change rather than an API change.
/// </remarks>
public enum RemovalStrategy
{
    /// <summary>A script drives the broker's own form, or calls its API.</summary>
    Automated,

    /// <summary>
    /// A script gets as far as it can and then parks, because the broker requires a step
    /// no script completes reliably.
    /// </summary>
    SemiAutomated,

    /// <summary>
    /// A jurisdiction-correct message to the broker's opt-out mailbox, after which the
    /// deadline does the waiting. For brokers offering no form at all.
    /// </summary>
    ManualEmail,
}
