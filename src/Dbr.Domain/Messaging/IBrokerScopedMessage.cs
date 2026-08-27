// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Messaging;

/// <summary>
/// Work that is addressed to one broker, and is therefore paced with everything else
/// addressed to that broker.
/// </summary>
/// <remarks>
/// <para>
/// The routing key of the whole system. Every tenant's work for one company lands in one
/// lane, so the company sees the pace the catalog allows however many accounts are queued
/// behind it — which is the point: a handful of tenants who happen to share a broker can,
/// in aggregate, look like a scraping attack while no individual tenant did anything
/// unusual.
/// </para>
/// <para>
/// A marker rather than a base class, because the two kinds of work that will implement it
/// have nothing else in common. Asking a broker what it holds and telling it to stop
/// holding it are different messages with different payloads; what they share is only that
/// they are addressed to the same company and must not both be sent at once.
/// </para>
/// </remarks>
public interface IBrokerScopedMessage
{
    /// <summary>The company this work is addressed to.</summary>
    Guid BrokerId { get; }
}
