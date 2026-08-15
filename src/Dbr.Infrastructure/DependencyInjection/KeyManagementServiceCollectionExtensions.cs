// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get key management.
/// </summary>
/// <remarks>
/// The one place a deployment chooses which key manager it uses. An operator who would
/// rather use a managed HSM replaces this call and changes nothing else — which only
/// stays true for as long as nothing outside this file mentions
/// <see cref="OpenBaoKeyManagementProvider"/> by name.
/// </remarks>
public static class KeyManagementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OpenBao Transit adapter against the <c>Bao</c> configuration
    /// section.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The settings cannot be used. Failing at startup matters more here than
    /// elsewhere: the alternative is discovering it at the moment somebody's identity
    /// is being written, with the write half-done.
    /// </exception>
    public static IServiceCollection AddDbrKeyManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new OpenBaoOptions();
        configuration.GetSection(OpenBaoOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);

        // A typed client, so the address and the token are attached once here rather
        // than by every call site remembering to. The factory also recycles handlers,
        // which a long-lived HttpClient built by hand does not — and this one talks to
        // a service the whole application depends on.
        services.AddHttpClient<IKeyManagementProvider, OpenBaoKeyManagementProvider>(client =>
        {
            client.BaseAddress = new Uri(options.Address);
            client.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
        });

        return services;
    }
}
