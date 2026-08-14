// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get sessions — issuing tokens, rotating them, and
/// taking them away.
/// </summary>
public static class SessionServiceCollectionExtensions
{
    /// <summary>
    /// Registers session handling against the <c>Tokens</c> configuration section.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The settings cannot be used. A missing signing key is the important one: this
    /// has no default, because a key committed to a public repository would mint valid
    /// tokens for every deployment that never replaced it.
    /// </exception>
    public static IServiceCollection AddDbrSessions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SessionTokenOptions();
        configuration.GetSection(SessionTokenOptions.SectionName).Bind(options);
        options.Validate();

        // Registered as an instance so that whatever validates tokens reads the same
        // settings that signed them. Two objects built from the same configuration
        // would agree until somebody changed only one of the places that builds one.
        services.AddSingleton(options);

        // The base gate, shared with passkey handling: a session may not be renewed
        // for an account that is no longer allowed to act. TryAdd because both
        // extensions need it and neither can assume it runs first.
        services.TryAddScoped<AccountGate>();

        services.AddScoped<RefreshTokenLookup>();
        services.AddScoped<SessionService>();

        return services;
    }
}
