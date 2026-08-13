// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Dbr.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dbr.Api.Authentication;

/// <summary>
/// Accepting the access tokens this service issues.
/// </summary>
public static class BearerAuthenticationExtensions
{
    public static IServiceCollection AddDbrBearerAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through the options system rather than inline so that the
        // settings come from the same object the issuing side was given. Building a
        // second one from configuration would work until somebody changed one of the
        // two places that builds one.
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureBearerFromSessionTokens>();

        services.AddAuthorization();

        return services;
    }

    private sealed class ConfigureBearerFromSessionTokens(SessionTokenOptions tokens)
        : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

        public void Configure(string? name, JwtBearerOptions options)
        {
            if (name != JwtBearerDefaults.AuthenticationScheme)
            {
                return;
            }

            // Claims arrive named as they were issued. The default is to rewrite the
            // standard short names into long WS-Federation URIs, so `sub` becomes a
            // claim type nothing in this codebase mentions — and looking for `sub`
            // then finds nothing, on a path where finding nothing means no tenant.
            options.MapInboundClaims = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = tokens.Issuer,

                ValidateAudience = true,
                ValidAudience = tokens.Audience,

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokens.SigningKey)),

                // Pinned rather than left to whatever the token asks for. The header
                // is written by whoever sends it, so accepting the algorithm it names
                // lets the sender choose how their own token gets checked.
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

                ValidateLifetime = true,

                // No grace. The default allows five minutes either side, which on a
                // fifteen-minute token is a third as long again — quietly undoing a
                // third of the reason these are short-lived in the first place. Both
                // ends of a token's life are decided by this service's own clock, so
                // there is no drift here to accommodate.
                ClockSkew = TimeSpan.Zero,
            };
        }
    }
}
