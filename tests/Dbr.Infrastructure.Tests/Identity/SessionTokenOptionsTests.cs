// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;

namespace Dbr.Infrastructure.Tests.Identity;

/// <summary>
/// The settings that decide how long a session lasts and what makes its tokens
/// trustworthy.
/// </summary>
public class SessionTokenOptionsTests
{
    private static SessionTokenOptions Valid() => new()
    {
        SigningKey = "a-signing-key-long-enough-to-satisfy-the-minimum",
    };

    [Fact]
    public void A_working_configuration_needs_only_a_signing_key()
    {
        // Everything else has a defensible default. The key does not, and cannot: one
        // shipped in the repository would mint valid tokens for every deployment that
        // never replaced it.
        Valid().Validate();
    }

    [Fact]
    public void A_missing_signing_key_stops_startup()
    {
        var options = new SessionTokenOptions();

        Assert.Contains("has no default", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_signing_key_too_short_for_the_algorithm_stops_startup()
    {
        // The interesting case, because it does not fail on its own. HMAC-SHA256 with
        // a twelve-byte key signs and verifies exactly as it should while being far
        // easier to forge than the code around it assumes.
        var options = new SessionTokenOptions { SigningKey = "too-short-yes" };

        Assert.Contains("at least", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void An_access_token_outliving_its_refresh_token_is_refused()
    {
        // Backwards, and quietly useless: by the time the access token needed
        // replacing, the thing that replaces it would already be gone.
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.FromDays(2);
        options.RefreshTokenLifetime = TimeSpan.FromDays(1);

        Assert.Contains("shorter than", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_refresh_token_outliving_the_session_cap_is_refused()
    {
        // The cap is the deadline rotation cannot move. A refresh token allowed to
        // outlast it would mean the cap never being what ends a session.
        var options = Valid();
        options.RefreshTokenLifetime = TimeSpan.FromDays(120);
        options.SessionLifetime = TimeSpan.FromDays(90);

        Assert.Contains("longer than", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_lifetime_of_zero_is_refused()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.Zero;

        Assert.Contains("must be positive", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
