// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Removals;

/// <summary>Where one removal request stands.</summary>
/// <remarks>
/// <para>
/// <see cref="Removed"/> is not the end. Brokers re-buy and re-scrape, so a listing
/// confirmed gone stays under periodic watch and comes back as <see cref="Reappeared"/> on
/// the request that already knows its history. The only states nothing leaves are
/// <see cref="Expired"/> and <see cref="Cancelled"/>.
/// </para>
/// <para>
/// <see cref="Cancelled"/> is not in §5's diagram, which is an omission in the diagram
/// rather than a decision: §6.5 gives a cancel route for a request still queued or
/// submitted, and a state machine with no state for it would have that endpoint writing a
/// status nothing else recognises.
/// </para>
/// </remarks>
public enum RemovalRequestStatus
{
    /// <summary>Accepted, waiting for a worker.</summary>
    Queued,

    /// <summary>Sent to the broker, outcome not yet known.</summary>
    Submitted,

    /// <summary>
    /// Parked until the tenant clears something no script can: a CAPTCHA, a confirmation
    /// link, an identity document.
    /// </summary>
    RequiresHumanInput,

    /// <summary>Sent, and the statutory or courtesy clock is running.</summary>
    AwaitingBrokerResponse,

    /// <summary>A verification scan confirmed the listing is gone.</summary>
    /// <remarks>Not terminal — see <see cref="Reappeared"/>.</remarks>
    Removed,

    /// <summary>A later verification scan found the listing again.</summary>
    Reappeared,

    /// <summary>This attempt did not work. Retryable while attempts remain.</summary>
    Failed,

    /// <summary>Retries exhausted. Terminal.</summary>
    Expired,

    /// <summary>The tenant called it off before it was answered. Terminal.</summary>
    Cancelled,
}
