// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using MassTransit;

namespace Dbr.Infrastructure.Messaging;

/// <summary>
/// What runs in every broker's lane.
/// </summary>
/// <remarks>
/// Consumers are named here rather than being discovered, because a consumer that ends up
/// in a per-broker lane by accident is a consumer being paced by a rule that has nothing
/// to do with it — and one that misses the lane talks to a broker at whatever speed it
/// likes. Both failures are invisible until a broker complains.
/// </remarks>
public sealed class BrokerLaneRegistrations
{
    internal List<Action<IBusRegistrationConfigurator>> Registrations { get; } = [];

    internal List<Action<IReceiveEndpointConfigurator, IBusRegistrationContext>> Bindings { get; } = [];

    /// <summary>Runs this consumer in every broker's lane.</summary>
    public BrokerLaneRegistrations Consume<TConsumer>()
        where TConsumer : class, IConsumer
    {
        Registrations.Add(bus => bus.AddConsumer<TConsumer>());
        Bindings.Add((endpoint, context) => endpoint.ConfigureConsumer<TConsumer>(context));

        return this;
    }
}

/// <summary>
/// Builds one receive endpoint per broker, paced by that broker's catalog row.
/// </summary>
/// <remarks>
/// <para>
/// Written against <see cref="IBusFactoryConfigurator"/> rather than against RabbitMQ's,
/// which is what lets the pacing and concurrency be exercised over an in-memory bus in a
/// test and over RabbitMQ in the worker without two descriptions of the same arrangement.
/// The transport is a deployment choice; the shape of the lanes is not.
/// </para>
/// <para>
/// One endpoint per broker rather than one endpoint partitioned by broker, because the
/// pacing differs per broker and a partitioner takes a single concurrency setting for all
/// of them. A few hundred queues is an ordinary number for RabbitMQ; a shared endpoint
/// that paced every company at whatever the twitchiest one needs is not an ordinary trade.
/// </para>
/// </remarks>
public static class BrokerLaneEndpoints
{
    /// <summary>Declares a lane for each broker.</summary>
    public static void Configure(
        IBusFactoryConfigurator bus,
        IBusRegistrationContext context,
        IReadOnlyList<BrokerLane> lanes,
        BrokerLaneRegistrations registrations)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(registrations);

        if (registrations.Bindings.Count == 0)
        {
            // Nothing to run in them, so no lanes. A queue standing open with nothing
            // draining it is worse than no queue: work would be accepted, acknowledged by
            // the transport, and sit there — which looks like a slow broker rather than
            // like a missing consumer. The bus still starts, so a process that only
            // publishes has somewhere to publish to.
            return;
        }

        foreach (var lane in lanes)
        {
            bus.ReceiveEndpoint(lane.QueueName, endpoint =>
            {
                // However many accounts are queued behind this company, it sees this many
                // at once and no more.
                endpoint.ConcurrentMessageLimit = lane.MaxConcurrency;

                // Prefetch is held to the concurrency limit rather than left at the
                // transport default. A lane allowed one job at a time that had pulled
                // thirty into memory would still run them one at a time, and would also
                // have taken thirty jobs off the queue that a second worker could have
                // been getting on with.
                endpoint.PrefetchCount = lane.MaxConcurrency;

                endpoint.UseFilter(new BrokerPacingFilter(lane));

                foreach (var bind in registrations.Bindings)
                {
                    bind(endpoint, context);
                }
            });
        }
    }
}
