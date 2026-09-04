// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Removals;
using Dbr.Infrastructure.Removals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get removal requests.
/// </summary>
public static class RemovalServiceCollectionExtensions
{
    /// <summary>Registers the removal service and the settings it reads.</summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c>, <c>AddDbrConsent</c> and <c>AddDbrCatalog</c> —
    /// the last because opening a demand resolves the governing regime and snapshots the
    /// deadline it produces. Deliberately not the vault or the key manager: deciding whether
    /// a demand may be opened, and against which company, reads only core-store columns, so
    /// this path never acquires the ability to decrypt anything. The identity itself is not
    /// needed until a connector is about to fill in a form, which asks for it with a grant.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public static IServiceCollection AddDbrRemovals(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RemovalOptions();

        configuration.GetSection(RemovalOptions.SectionName).Bind(options);

        // At startup rather than at the first retry. A retry budget of zero is a setting
        // that looks harmless in a config file and only shows itself the day somebody
        // needs the thing it disabled.
        options.Validate();

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddScoped<IRemovalService, RemovalService>();

        return services;
    }
}
