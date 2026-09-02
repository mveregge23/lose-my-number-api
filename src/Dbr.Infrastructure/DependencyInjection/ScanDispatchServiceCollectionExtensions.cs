// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Search;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Search;
using Dbr.Search;
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
        services.AddDbrSearchRegistry(configuration);

        services.AddScoped<ScanCompletion>();
        services.AddScoped<IScanDispatcher, ScanDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers whichever searches this build knows how to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A registration of its own, and this is the story where it stopped answering nothing.
    /// Both sides of a leg resolve through it — the dispatcher, to learn what a search needs
    /// before minting a grant for it, and the handler, to run it — so a build that registered
    /// two different registries would mint against one declaration and search with another.
    /// </para>
    /// <para>
    /// <b>The recipes are read here and a bad one stops the process.</b> Failing at startup is
    /// the only place it can usefully fail: a malformed recipe discovered when a leg runs is a
    /// company already being sent a request built out of it, and the alternative to stopping
    /// is a worker that quietly searches fewer companies than its catalog says it can.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A recipe cannot be used as written.</exception>
    public static IServiceCollection AddDbrSearchRegistry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SearchHttpOptions();
        configuration.GetSection(SearchHttpOptions.SectionName).Bind(options);
        options.Validate();

        var read = SearchRecipeReader.Read();

        if (read.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The search recipes compiled into this build cannot be used as written:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, read.Problems.Select(problem => "  " + problem)));
        }

        services.AddSingleton(options);

        services.AddHttpClient(SearchClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            // Who is asking, and where to read about why. The convention costs nothing and is
            // the difference between an operator finding an explanation and finding an
            // unidentified scraper.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });

        // One engine per company, shared across every tenant's legs — it holds a recipe and a
        // client and nothing about a person, so there is nothing in it to leak between them.
        // TryAdd, so a build that has registered searches of its own keeps them.
        services.TryAddSingleton<IBrokerSearchRegistry>(provider =>
        {
            var clients = provider.GetRequiredService<IHttpClientFactory>();

            return new RecipeSearchRegistry(
                read.Recipes,
                recipe => new GenericWebSearchConnector(
                    recipe,
                    clients.CreateClient(SearchClientName),

                    // Where a request goes is settled here, in code, and never by the
                    // document. A recipe writes a path; this is what turns a catalog domain
                    // into an origin, and https is not negotiable — a search carries part of
                    // somebody's identity in its query string.
                    target => new Uri($"https://{target.Domain}")));
        });

        return services;
    }

    /// <summary>The named client every recipe-tier search goes out on.</summary>
    private const string SearchClientName = "dbr-search";
}
