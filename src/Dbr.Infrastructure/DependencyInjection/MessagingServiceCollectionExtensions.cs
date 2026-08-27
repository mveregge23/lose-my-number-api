// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using Dbr.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get the per-broker lanes.
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the bus, with one receive endpoint per active broker paced by its catalog
    /// row.
    /// </summary>
    /// <param name="lanes">
    /// What runs in each lane. A process with nothing to say to brokers passes nothing and
    /// gets a bus with no endpoints, which is the honest arrangement while the consumers
    /// are still being written.
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

        // Read once, here, and blocking. Bus configuration is not an async context, and
        // this is startup: a process that cannot reach the catalog has no pacing to apply
        // and should fail now rather than talk to brokers at a speed nobody chose.
        var declared = directory
            .ListLanesAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        services.AddMassTransit(bus =>
        {
            foreach (var register in registrations.Registrations)
            {
                register(bus);
            }

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

        return services;
    }
}
