// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Consent;
using Dbr.Infrastructure.Consent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get consent records.
/// </summary>
public static class ConsentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the consent service against the <c>Consent</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and nothing else. Consent is a fact about an
    /// account rather than about an identity, so this path never touches the vault or
    /// the key manager — which is what lets a dispatcher check it without acquiring the
    /// ability to decrypt anything.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No policy version is configured. Failing here rather than at the first decision:
    /// an instance that cannot say which consent text it serves cannot record an
    /// agreement to it, and finding that out when somebody flips a switch is finding it
    /// out from them.
    /// </exception>
    public static IServiceCollection AddDbrConsent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ConsentPolicyOptions();
        configuration.GetSection(ConsentPolicyOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddScoped<IConsentService, ConsentService>();

        return services;
    }
}
