// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Removals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What the worker calls to get demands sent.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>AddDbrRemovals</c> and not called by the API, for the reason the scan
/// dispatch registration is separate: finding demands nobody has sent reaches past the
/// tenant boundary, and the process serving requests has no business being able to do it. A
/// registration it never uses is still a capability sitting in the container.
/// </para>
/// <para>
/// <b>What it does not require is again the point.</b> Not the vault and not the key
/// manager. Dispatching a demand claims a row, writes an attempt and mints a grant — and
/// minting is a row of random bytes against the core store, so the process that sends
/// demands never acquires the ability to open one. That matters more here than on the scan
/// side, because this is the process that will drive a browser against a company's site.
/// </para>
/// </remarks>
public static class RemovalDispatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queued-demand directory, the dispatcher, and the connector registry
    /// both sides of an attempt resolve against.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and <c>AddDbrMessaging</c> — the second because a
    /// dispatcher with nowhere to put an attempt cannot do anything.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The core connection string is absent, or the settings cannot work as given.
    /// </exception>
    public static IServiceCollection AddDbrRemovalDispatch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var dispatch = new RemovalDispatchOptions();
        configuration.GetSection(RemovalDispatchOptions.SectionName).Bind(dispatch);
        dispatch.Validate();

        // The retry budget is read here as well as by the API, and from the same section.
        // Two processes disagreeing about how many attempts a demand gets would have one of
        // them requeue what the other had given up on.
        var removals = new RemovalOptions();
        configuration.GetSection(RemovalOptions.SectionName).Bind(removals);
        removals.Validate();

        var connectionString = configuration.GetConnectionString(
            InfrastructureServiceCollectionExtensions.CoreConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No core database connection string, so queued demands cannot be found. See "
                + "AddDbrPersistence for the setting; this reads the same one, through a role "
                + "that may do nothing but list demands nobody has sent.");
        }

        services.AddSingleton(dispatch);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(removals));
        services.AddSingleton<IQueuedRemovalDirectory>(new QueuedRemovalDirectory(connectionString));

        services.AddDbrConnectorRegistry();

        // Minting, and deliberately not redeeming. Writing down that an attempt may see part
        // of an identity is a row of random bytes against the core store; opening one needs
        // the keys, which this process does not have and asks the edge for.
        services.AddDbrReleaseMinting(configuration);

        services.AddScoped<IRemovalDispatcher, RemovalDispatcher>();
        services.AddScoped<RemovalJobWorkHandler>();

        return services;
    }

    /// <summary>
    /// Registers whichever connectors this build knows how to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A registration of its own because both sides of an attempt resolve through it — the
    /// dispatcher, to learn whether a company can be asked at all and under what name the
    /// attempt is recorded, and the handler, to run it. A build registering two different
    /// registries would dispatch against one and run the other.
    /// </para>
    /// <para>
    /// <b>It is empty, and that is the honest state of this build.</b> No connector exists
    /// yet: the generic web-form engine and the templated-email one are their own stories.
    /// An empty registry means every demand stays queued rather than being sent somewhere
    /// wrong, which is the behaviour the dispatcher is written around. <c>TryAdd</c>, so a
    /// build that has registered connectors of its own keeps them — which is how a test
    /// puts one in.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDbrConnectorRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBrokerConnectorRegistry>(new EmptyBrokerConnectorRegistry());

        return services;
    }
}

/// <summary>
/// A registry with nothing in it.
/// </summary>
/// <remarks>
/// A named type rather than a lambda so that what it means is visible in a container dump
/// and in a stack trace: a deployment whose demands all stay queued should be able to find
/// out why by looking at what is registered.
/// </remarks>
internal sealed class EmptyBrokerConnectorRegistry : IBrokerConnectorRegistry
{
    public ConnectorRegistration? Find(Guid brokerId) => null;
}
