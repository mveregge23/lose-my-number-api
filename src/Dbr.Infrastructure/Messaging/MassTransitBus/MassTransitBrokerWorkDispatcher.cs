// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using MassTransit;

namespace Dbr.Infrastructure.Messaging.MassTransitBus;

/// <summary>
/// Puts work into a broker's lane, over MassTransit.
/// </summary>
/// <remarks>
/// Addressed by queue name derived from the message's own broker rather than published to
/// a topic. Publishing would route by message type, which is the one thing that must not
/// decide the lane here — two brokers' work is the same type and belongs in different
/// queues, paced differently.
/// </remarks>
public sealed class MassTransitBrokerWorkDispatcher(ISendEndpointProvider endpoints)
    : IBrokerWorkDispatcher
{
    public async Task DispatchAsync<TWork>(TWork work, CancellationToken cancellationToken)
        where TWork : class, IBrokerScopedMessage
    {
        ArgumentNullException.ThrowIfNull(work);

        var endpoint = await endpoints
            .GetSendEndpoint(new Uri($"queue:{BrokerLane.QueueNameFor(work.BrokerId)}"))
            .ConfigureAwait(false);

        await endpoint.Send(work, cancellationToken).ConfigureAwait(false);
    }
}
