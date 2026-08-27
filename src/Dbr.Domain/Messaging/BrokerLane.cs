// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Messaging;

/// <summary>
/// One broker's lane, and the pace the catalog says to talk to it at.
/// </summary>
/// <remarks>
/// <para>
/// Read from the catalog row rather than configured globally, because a company known to
/// be twitchy about automated traffic earns a stricter lane than one that has never
/// minded. The defaults behind these are the slowest lane there is — one job at a time, a
/// second between them — so a broker added without anybody thinking about pacing is
/// gentler than intended rather than more aggressive.
/// </para>
/// <para>
/// The lane is named by id rather than by domain. A domain is a catalog field that can be
/// corrected; a queue named after one would be orphaned by the correction, with whatever
/// was in it still inside. Legibility in a management console is worth less than not
/// stranding somebody's queued removal.
/// </para>
/// </remarks>
/// <param name="MaxConcurrency">How many of this broker's jobs may run at once.</param>
/// <param name="MinDelay">The least time between two of them starting.</param>
public sealed record BrokerLane(Guid BrokerId, int MaxConcurrency, TimeSpan MinDelay)
{
    /// <summary>The queue this lane's work lands in.</summary>
    public string QueueName => $"broker-{BrokerId:D}";
}

/// <summary>
/// Which brokers have lanes, and how each is paced.
/// </summary>
/// <remarks>
/// Read once, when the bus is configured. A broker added later has no lane until the
/// process restarts — which is acceptable rather than ideal, and acceptable for a specific
/// reason: catalog content arrives by the sync that runs at deploy time, and a deploy
/// restarts the worker. A broker appearing without a deploy is not a path that exists yet.
/// </remarks>
public interface IBrokerLaneDirectory
{
    /// <summary>Every active broker's lane.</summary>
    Task<IReadOnlyList<BrokerLane>> ListLanesAsync(CancellationToken cancellationToken);
}
