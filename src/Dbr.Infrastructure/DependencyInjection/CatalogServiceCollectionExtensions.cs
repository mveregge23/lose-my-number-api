// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Infrastructure.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to read the broker catalog.
/// </summary>
public static class CatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the catalog reader.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and nothing else, and takes no configuration.
    /// There is nothing to configure: the catalog is the same rows for every caller, and
    /// the one thing an operator decides about it — what is in it — is decided by what
    /// the curated content applies, not by this process.
    /// </remarks>
    public static IServiceCollection AddDbrCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICatalogService, CatalogService>();

        return services;
    }
}
