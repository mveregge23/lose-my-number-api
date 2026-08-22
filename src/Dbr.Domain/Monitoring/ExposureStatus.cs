// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>Where one finding stands.</summary>
/// <remarks>
/// <see cref="Removed"/> is not a terminal state, which is the whole reason monitoring
/// is recurring rather than a one-off: brokers re-buy and re-scrape, so a listing that
/// went away can come back, and it comes back as <see cref="Reappeared"/> on the row
/// that already knows its history rather than as a new finding with none.
/// </remarks>
public enum ExposureStatus
{
    /// <summary>Found, and nothing has been asked of the broker yet.</summary>
    New,

    /// <summary>A removal request is open against it.</summary>
    Requested,

    /// <summary>A verification scan confirmed it is gone.</summary>
    Removed,

    /// <summary>A later scan found it again after it had been removed.</summary>
    Reappeared,

    /// <summary>The tenant says this is not them.</summary>
    /// <remarks>
    /// A judgement by the only person who can make it. Nothing is sent in somebody's
    /// name over a match they have told us is somebody else.
    /// </remarks>
    Dismissed,
}
