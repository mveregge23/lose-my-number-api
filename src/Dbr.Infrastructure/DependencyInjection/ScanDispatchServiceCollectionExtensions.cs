// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Search;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to turn queued scans into work in the lanes.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>AddDbrMonitoring</c> and not called by the API, for the reason the
/// scheduling registration is separate: finding runs nobody has started reaches past the
/// tenant boundary, and the process serving requests has no business being able to do it.
/// A registration it never uses is still a capability sitting in the container.
/// </para>
/// <para>
/// <b>What it does not require is the point.</b> Not the vault, not the key manager.
/// Dispatching mints a grant, and minting writes a row of random bytes against the core
/// store — so the process that fans scans out can plan the work without ever being able to
/// open it.
/// </para>
/// </remarks>
public static class ScanDispatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queued-run directory, the dispatcher, and the search registry it
    /// resolves against.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and <c>AddDbrMessaging</c> — the second because a
    /// dispatcher with nowhere to put a leg is a dispatcher that cannot do anything.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The core connection string is absent, or the dispatch settings cannot work.
    /// </exception>
    public static IServiceCollection AddDbrScanDispatch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ScanDispatchOptions();
        configuration.GetSection(ScanDispatchOptions.SectionName).Bind(options);
        options.Validate();

        var connectionString = configuration.GetConnectionString(
            InfrastructureServiceCollectionExtensions.CoreConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No core database connection string, so queued scans cannot be found. See "
                + "AddDbrPersistence for the setting; this reads the same one, through a role "
                + "that may do nothing but list runs nobody has started.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IQueuedScanDirectory>(new QueuedScanDirectory(connectionString));

        services.AddDbrReleaseMinting(configuration);
        services.AddDbrSearchRegistry();

        services.AddScoped<ScanCompletion>();
        services.AddScoped<IScanDispatcher, ScanDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers whichever searches this build knows how to run.
    /// </summary>
    /// <remarks>
    /// A registration of its own so that the day there are real searches, they arrive here
    /// and nothing else moves. Both sides of a leg resolve through it — the dispatcher, to
    /// learn what a search needs before minting a grant for it, and the handler, to run
    /// it — so a build that registered two different registries would mint against one
    /// declaration and search with another.
    /// </remarks>
    public static IServiceCollection AddDbrSearchRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Stateless and resolved once per leg on both sides, so a singleton. TryAdd, so a
        // build that has registered real searches keeps them.
        services.TryAddSingleton<IBrokerSearchRegistry, EmptyBrokerSearchRegistry>();

        return services;
    }
}
