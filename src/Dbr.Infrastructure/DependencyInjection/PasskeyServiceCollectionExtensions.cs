// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;
using Fido2NetLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get passkey registration and login.
/// </summary>
public static class PasskeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers passkey registration and login against the <c>Passkeys</c> configuration section.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The settings cannot work as given. Failing here is deliberate: every one of
    /// those mistakes otherwise surfaces as a browser refusing a ceremony, on a path
    /// that needs a real authenticator to reach and reports nothing about which
    /// setting was wrong.
    /// </exception>
    public static IServiceCollection AddDbrPasskeys(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new PasskeyOptions();
        configuration.GetSection(PasskeyOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);

        // One instance: it holds configuration and no per-request state, and building
        // it per request would re-read nothing that changes.
        services.AddSingleton<IFido2>(new Fido2(new Fido2Configuration
        {
            ServerDomain = options.RelyingPartyId,
            ServerName = options.RelyingPartyName,
            Origins = options.Origins.ToHashSet(StringComparer.OrdinalIgnoreCase),

            // What the browser is told, in milliseconds. Shorter than the server's own
            // expiry so that a prompt nobody is answering gives up before the
            // challenge behind it stops being accepted — the reverse leaves someone
            // completing a ceremony the server has already forgotten.
            Timeout = (uint)(options.CeremonyLifetime.TotalMilliseconds / 2),
        }));

        // Scoped, because each takes the request's DbContext and therefore its tenant.
        services.AddScoped<PasskeyCeremonyStore>();
        services.AddScoped<PasskeyLookup>();
        services.AddScoped<PasskeyService>();

        return services;
    }
}
