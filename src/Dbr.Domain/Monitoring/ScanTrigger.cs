// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>What caused a scan to exist.</summary>
/// <remarks>
/// Kept on the row rather than inferred from whether anything was passed with the
/// request. A tenant asking now and a schedule firing on their behalf produce the same
/// work, and the difference is the one somebody wants back when they ask why their data
/// was searched for on a day they did not touch the application.
/// </remarks>
public enum ScanTrigger
{
    /// <summary>Somebody asked for it.</summary>
    Manual,

    /// <summary>The recurring cadence asked for it.</summary>
    Scheduled,
}
