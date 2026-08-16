// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get the vault store and the service in front of it.
/// </summary>
/// <remarks>
/// Registered by the API and deliberately not by the Worker. A worker that talks to
/// broker sites holding a connection that can read identity fields is a standing
/// decryption right in the process most exposed to the outside world — the same reason
/// it holds no key-manager credentials. What a worker gets instead is a short-lived,
/// job-scoped release of the fields one job needs, granted by the service that does
/// hold both.
/// </remarks>
public static class VaultServiceCollectionExtensions
{
    /// <summary>
    /// Configuration key holding the vault store's connection string. Set in compose as
    /// <c>ConnectionStrings__Vault</c>.
    /// </summary>
    /// <remarks>
    /// Its own key rather than a schema on the core one, because this is the seam the
    /// vault moves along: pointing it at another database — or at the same one under a
    /// user with different rights — is a configuration change and nothing else.
    /// </remarks>
    public const string VaultConnectionStringName = "Vault";

    /// <summary>
    /// Registers <see cref="VaultDbContext"/> and the profile service in front of it.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and <c>AddDbrKeyManagement</c>: a profile is
    /// stored across both stores, and its fields are unreadable without the key manager.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The vault connection string is absent or blank.
    /// </exception>
    public static IServiceCollection AddDbrVault(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(VaultConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No vault database connection string. Set ConnectionStrings:{VaultConnectionStringName} " +
                $"(ConnectionStrings__{VaultConnectionStringName} as an environment variable). " +
                "It is separate from the core one even while both point at the same database — " +
                "that separation is what lets the vault move later. docker-compose.yml sets both " +
                "for the API; running outside compose means supplying them yourself.");
        }

        // The same tenant object the core store's interceptor reads. One unit of work
        // acts for one account, in both stores or in neither.
        services.AddTenantContext();

        services.AddScoped<VaultSessionInterceptor>();

        services.AddDbContext<VaultDbContext>((sp, options) => options
            .UseDbr(connectionString)
            .AddInterceptors(sp.GetRequiredService<VaultSessionInterceptor>()));

        services.AddScoped<IProfileService, ProfileService>();

        return services;
    }
}
