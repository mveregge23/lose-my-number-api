// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>How an attempt to request a scan ended.</summary>
public enum RequestScanOutcome
{
    /// <summary>Accepted and waiting to run.</summary>
    Queued,

    /// <summary>
    /// The tenant has not permitted scanning, or has withdrawn that permission.
    /// </summary>
    /// <remarks>
    /// Checked here rather than trusted to the client, and checked at the moment of
    /// asking rather than at signup: a permission that was granted once and withdrawn
    /// since is exactly the case a stale check would get wrong, in the direction of
    /// searching for somebody who told us to stop.
    /// </remarks>
    ConsentMissing,

    /// <summary>
    /// No such profile for this tenant.
    /// </summary>
    /// <remarks>
    /// One outcome for "no such profile" and "somebody else's profile" deliberately.
    /// Telling those apart would confirm that an id belongs to another account, which is
    /// the same reason the profile service answers them alike.
    /// </remarks>
    ProfileNotFound,

    /// <summary>One of the named brokers is not in this instance's catalog.</summary>
    /// <remarks>
    /// Refused rather than narrowed to the ones that do exist. Dropping the unknown ids
    /// would run a scan over a smaller set than was asked for and report it as the scan
    /// that was asked for, and a client would have no way to tell.
    /// </remarks>
    UnknownBroker,
}

/// <param name="Scan">The queued run, or <see langword="null"/> when nothing was queued.</param>
/// <param name="UnknownBrokerIds">
/// The ids that are not in the catalog, when that is why this failed. Every one of them
/// rather than the first, since somebody fixing a request wants the whole list.
/// </param>
public sealed record RequestScanResult(
    RequestScanOutcome Outcome,
    Scan? Scan,
    IReadOnlyList<Guid> UnknownBrokerIds)
{
    public static RequestScanResult Queued(Scan scan) =>
        new(RequestScanOutcome.Queued, scan, []);

    public static RequestScanResult Failed(RequestScanOutcome outcome) =>
        new(outcome, null, []);

    public static RequestScanResult Unknown(IReadOnlyList<Guid> brokerIds) =>
        new(RequestScanOutcome.UnknownBroker, null, brokerIds);
}

/// <summary>A scan and the brokers it was narrowed to.</summary>
/// <param name="BrokerIds">
/// Empty when the scan was not narrowed, which means the whole catalog rather than no
/// brokers.
/// </param>
public sealed record ScanDetail(Scan Scan, IReadOnlyList<Guid> BrokerIds);
