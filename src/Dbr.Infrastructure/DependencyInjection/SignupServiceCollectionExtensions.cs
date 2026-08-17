// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get account signup.
/// </summary>
public static class SignupServiceCollectionExtensions
{
    /// <summary>
    /// Registers signup against the <c>Terms</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPasskeys</c> and <c>AddDbrVault</c>: opening an account
    /// registers a credential and creates the account's own profile, and the profile is
    /// half of what an account is rather than an extra a deployment could leave out.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No terms version is configured. Failing here rather than at the first signup: an
    /// instance that cannot say which terms it serves cannot record an acceptance of
    /// them, and finding that out when somebody tries to open an account is finding it
    /// out from them.
    /// </exception>
    public static IServiceCollection AddDbrSignup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new TermsOptions();
        configuration.GetSection(TermsOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddScoped<SignupService>();

        return services;
    }
}
