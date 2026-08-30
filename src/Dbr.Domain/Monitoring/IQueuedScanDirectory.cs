// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <param name="TenantId">The account to act as in order to start it.</param>
public sealed record QueuedScan(Guid ScanId, Guid TenantId);

/// <summary>
/// Which runs are waiting, and whose — the other question no tenant-scoped role can
/// answer.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of the account directory, and narrow for the same reason. A scan is
/// recorded as queued by whoever asked for it and left there; what starts it is a process
/// acting for nobody in particular, which has to find out what is waiting before it can
/// act for anybody. That is a privilege the rest of the system deliberately does not have,
/// so the thing holding it is one method returning two ids.
/// </para>
/// <para>
/// <b>It sees waiting runs and nothing else.</b> Not a claimed one, not a finished one, and
/// nothing about what any of them is searching for. So it cannot be used to watch what an
/// account is doing — only to find work nobody has picked up, which is the whole of what
/// it is for.
/// </para>
/// <para>
/// Everything that follows from the answer goes back through the ordinary tenant-scoped
/// path, one account at a time. This answers what is waiting; it never answers anything
/// about the person it is waiting for.
/// </para>
/// </remarks>
public interface IQueuedScanDirectory
{
    /// <summary>Runs that have been asked for and not yet started.</summary>
    /// <param name="limit">
    /// How many to take. A sweep is a batch rather than a drain: an instance with a large
    /// backlog should make progress on each pass and leave the rest, so that one enormous
    /// wake-up does not hold every broker lane open at once.
    /// </param>
    Task<IReadOnlyList<QueuedScan>> ListQueuedAsync(int limit, CancellationToken cancellationToken);
}
