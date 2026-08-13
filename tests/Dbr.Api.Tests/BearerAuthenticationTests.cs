// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Authentication;
using Dbr.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dbr.Api.Tests;

/// <summary>
/// How an access token is checked on the way back in.
/// </summary>
/// <remarks>
/// Three of these settings are worth asserting because their absence is invisible:
/// tokens still validate, requests still succeed, and the guarantee is quietly weaker
/// than the code around it assumes. A test is the only thing that notices.
/// </remarks>
public class BearerAuthenticationTests
{
    private static JwtBearerOptions Configured()
    {
        var services = new ServiceCollection();

        services.AddSingleton(new SessionTokenOptions
        {
            SigningKey = "a-signing-key-long-enough-to-satisfy-the-minimum",
            Issuer = "dbr-test",
            Audience = "dbr-test-audience",
        });

        services.AddLogging();
        services.AddDbrBearerAuthentication();

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void An_expired_token_is_expired_with_no_grace_period()
    {
        // The default is five minutes either side, which on a fifteen-minute token is
        // a third as long again — quietly returning a third of what short-lived access
        // tokens were meant to take away. Both ends of the lifetime are set by this
        // service's own clock, so there is no drift to accommodate.
        Assert.Equal(TimeSpan.Zero, Configured().TokenValidationParameters.ClockSkew);
    }

    [Fact]
    public void Only_the_algorithm_this_service_signs_with_is_accepted()
    {
        // The algorithm is named in a header the sender writes. Accepting whatever it
        // says lets the sender choose how their own token gets checked, which is the
        // shape of every algorithm-confusion attack.
        Assert.Equal(
            [SecurityAlgorithms.HmacSha256],
            Configured().TokenValidationParameters.ValidAlgorithms);
    }

    [Fact]
    public void Claims_keep_the_names_they_were_issued_with()
    {
        // On by default, this rewrites `sub` into a WS-Federation URI. Nothing in this
        // codebase looks for that URI, so the tenant claim would simply not be found —
        // and not finding it is indistinguishable from a request that never had one.
        Assert.False(Configured().MapInboundClaims);
    }

    [Fact]
    public void Issuer_audience_lifetime_and_signature_are_all_checked()
    {
        var parameters = Configured().TokenValidationParameters;

        Assert.True(parameters.ValidateIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.Equal("dbr-test", parameters.ValidIssuer);
        Assert.Equal("dbr-test-audience", parameters.ValidAudience);
    }
}
