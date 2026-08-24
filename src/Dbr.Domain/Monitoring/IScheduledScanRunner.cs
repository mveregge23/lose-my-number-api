// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <param name="Queued">Scans written by this run.</param>
/// <param name="AlreadyQueued">
/// Identities that already had one for today, so nothing was written. Counted rather than
/// ignored: a run reporting all-already-queued is how a doubled scheduler looks, and it
/// should be visible without being an error.
/// </param>
/// <param name="ConsentMissing">
/// The account has not permitted scanning. Not a failure — it is the switch working.
/// </param>
public sealed record ScheduledScanRun(int Queued, int AlreadyQueued, bool ConsentMissing)
{
    public static ScheduledScanRun Refused() => new(0, 0, true);
}

/// <summary>
/// Queues the recurring scans for whichever account the current scope acts for.
/// </summary>
/// <remarks>
/// Takes no tenant, like every other service here. The scheduler establishes one per
/// account before calling this, so the work runs inside the same boundary as a scan
/// somebody asked for by hand — same role, same policies, same consent check. The only
/// thing the scheduler does that a request cannot is decide whose turn it is.
/// </remarks>
public interface IScheduledScanRunner
{
    /// <summary>Queues a scan for each identity this account manages.</summary>
    Task<ScheduledScanRun> RunAsync(CancellationToken cancellationToken);
}
