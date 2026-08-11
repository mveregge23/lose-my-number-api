// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root (§2.1) calls to get the core persistence layer.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Configuration key holding the core store's connection string. Set in compose
    /// as <c>ConnectionStrings__Core</c>; the name is <c>Core</c> rather than
    /// <c>Default</c> because §4 splits the vault into its own store, which will get
    /// its own connection string — and, per §18.4, its own migration journal.
    /// </summary>
    public const string CoreConnectionStringName = "Core";

    /// <summary>
    /// Registers <see cref="DbrDbContext"/> against the core Postgres store.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The core connection string is absent or blank. Failing at startup is
    /// deliberate: a service that boots without a database and only discovers it on
    /// the first request looks healthy to an orchestrator while it is not.
    /// </exception>
    public static IServiceCollection AddDbrPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(CoreConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No core database connection string. Set ConnectionStrings:{CoreConnectionStringName} " +
                $"(ConnectionStrings__{CoreConnectionStringName} as an environment variable). " +
                "docker-compose.yml sets it for every service in the stack; running outside compose " +
                "means supplying it yourself — see the README quickstart.");
        }

        // AddDbContext, not AddDbContextPool: pooling reuses context instances across
        // requests, and DBR-004 is about to attach per-request tenant state to the
        // connection. Pooling that correctly is possible but is a sharp edge on the
        // one boundary §4 says has to fail closed. Revisit under load, not before.
        services.AddDbContext<DbrDbContext>(options => options.UseDbr(connectionString));

        return services;
    }
}
