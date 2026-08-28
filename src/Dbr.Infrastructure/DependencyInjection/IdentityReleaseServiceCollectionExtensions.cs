// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to be able to hand out scoped access to an identity.
/// </summary>
/// <remarks>
/// <b>Only a process that already holds the keys should call this.</b> Registering it
/// requires the vault and the key manager, because redeeming a grant decrypts — so a
/// worker that called this would be a worker that could decrypt, which is the thing the
/// grant exists to avoid. That dependency is enforced by what the service needs rather
/// than by a comment: <c>AddDbrVault</c> is what supplies the profile service behind it,
/// and a composition root without it fails to build the container.
/// </remarks>
public static class IdentityReleaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers minting and redeeming against the <c>IdentityRelease</c> configuration
    /// section.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The lifetime cannot work. Failing at startup rather than at the moment a grant is
    /// minted: a zero lifetime refuses every redemption and looks like a broken worker,
    /// and an over-long one is standing access nobody notices.
    /// </exception>
    public static IServiceCollection AddDbrIdentityReleases(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new IdentityReleaseOptions();
        configuration.GetSection(IdentityReleaseOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);

        // Expiry is the whole of what makes a grant short-lived, so this path needs a
        // clock it can be tested against. Registered only if nothing else has — a host
        // that brought its own, as the worker does, should keep it rather than have one
        // call quietly replace what the rest of the process reads the time from.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IdentityReleaseLookup>();
        services.AddScoped<IIdentityReleaseService, IdentityReleaseService>();

        return services;
    }
}
