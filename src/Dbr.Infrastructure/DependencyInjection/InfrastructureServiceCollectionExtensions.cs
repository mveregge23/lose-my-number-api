// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get the core persistence layer.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Configuration key holding the core store's connection string. Set in compose
    /// as <c>ConnectionStrings__Core</c>. The name is <c>Core</c> rather than
    /// <c>Default</c> because the vault is a separate store with its own connection
    /// string and its own migration journal; calling this one "default" would make
    /// the other look like an exception to something.
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

        services.AddTenantContext();
        services.AddScoped<TenantSessionInterceptor>();

        // AddDbContext, not AddDbContextPool: pooling reuses context instances across
        // requests, and the interceptor below attaches per-request tenant state to the
        // connection. Pooling that correctly is possible, but it puts a sharp edge on
        // the tenant boundary, where a mistake means one tenant reading another's
        // rows. Revisit under measured load, not before.
        services.AddDbContext<DbrDbContext>((sp, options) => options
            .UseDbr(connectionString)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        return services;
    }

    /// <summary>
    /// The current tenant, shared by every store.
    /// </summary>
    /// <remarks>
    /// Scoped to the unit of work — an API request, or one consumed message. Both
    /// registrations resolve to the same instance so whoever establishes the tenant
    /// writes to the object every interceptor reads; a second copy would mean one store
    /// acting for an account and the other for nobody. Added by whichever store is
    /// registered first, which is why it is a <c>TryAdd</c>.
    /// </remarks>
    internal static IServiceCollection AddTenantContext(this IServiceCollection services)
    {
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        return services;
    }
}
