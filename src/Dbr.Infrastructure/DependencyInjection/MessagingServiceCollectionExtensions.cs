// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using Dbr.Infrastructure.Messaging;
using Dbr.Infrastructure.Messaging.MassTransitBus;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get the per-broker lanes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing a caller passes here names the library carrying the messages.</b> Work and
/// handlers are named in the project's own terms; which bus moves them is settled inside
/// this method and inside one folder beside it. That is §1's rule applied to a queue: a
/// vendor's software sits behind an interface the core never bypasses, so replacing it is
/// a registration change rather than a rewrite.
/// </para>
/// <para>
/// It is not a hypothetical concern. MassTransit v9 requires a commercial licence, so the
/// pin here is the last Apache-2.0 line — which is a decision with a shelf life, and the
/// reason the seam is worth having before it is needed rather than after.
/// </para>
/// </remarks>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the bus, with one receive endpoint per active broker paced by its catalog
    /// row.
    /// </summary>
    /// <param name="lanes">
    /// What runs in each lane. A process with nothing to say to brokers passes nothing and
    /// gets a bus it can dispatch through and no endpoints, which is the honest
    /// arrangement while the handlers are still being written.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The queue or the database is not configured.
    /// </exception>
    public static IServiceCollection AddDbrMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BrokerLaneRegistrations>? lanes = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RabbitMqOptions();
        configuration.GetSection(RabbitMqOptions.SectionName).Bind(options);
        options.Validate();

        var connectionString = configuration.GetConnectionString(
            InfrastructureServiceCollectionExtensions.CoreConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No core database connection string, so the broker lanes cannot be built — how "
                + "fast to talk to each company is a catalog row, not a setting.");
        }

        var registrations = new BrokerLaneRegistrations();
        lanes?.Invoke(registrations);

        var directory = new BrokerLaneDirectory(connectionString);

        services.AddSingleton(options);
        services.AddSingleton<IBrokerLaneDirectory>(directory);

        foreach (var work in registrations.Work)
        {
            services.AddScoped(typeof(IBrokerWorkHandler<>).MakeGenericType(work.Work), work.Handler);
        }

        // Read once, here, and blocking. Bus configuration is not an async context, and
        // this is startup: a process that cannot reach the catalog has no pacing to apply
        // and should fail now rather than talk to brokers at a speed nobody chose.
        var declared = directory
            .ListLanesAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        services.AddMassTransit(bus =>
        {
            BrokerLaneEndpoints.Register(bus, registrations);

            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });

                BrokerLaneEndpoints.Configure(rabbit, context, declared, registrations);
            });
        });

        services.AddScoped<IBrokerWorkDispatcher, MassTransitBrokerWorkDispatcher>();

        return services;
    }
}
