// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;

namespace Dbr.Infrastructure.Tests.Identity;

/// <summary>
/// The settings that decide whether passkeys work at all.
/// </summary>
/// <remarks>
/// Worth testing because every mistake here has the same symptom — a browser refusing
/// the ceremony without saying why — and reaching that symptom needs a real
/// authenticator. Startup is the only place these can fail usefully.
/// </remarks>
public class PasskeyOptionsTests
{
    [Fact]
    public void The_defaults_describe_the_local_stack_and_are_valid()
    {
        // The self-hosted quickstart is `docker compose up` and nothing else, so the
        // out-of-the-box configuration has to be one that actually starts.
        new PasskeyOptions().Validate();
    }

    [Fact]
    public void A_relying_party_written_as_a_url_is_refused()
    {
        // The likeliest mistake, because every other origin setting in a config file
        // is a URL. The browser compares this against the origin's host, so a scheme
        // here means nothing ever matches.
        var options = new PasskeyOptions
        {
            RelyingPartyId = "https://example.com",
            Origins = ["https://example.com"],
        };

        Assert.Contains("bare host", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void An_origin_outside_the_relying_party_is_refused()
    {
        var options = new PasskeyOptions
        {
            RelyingPartyId = "example.com",
            Origins = ["https://example.org"],
        };

        Assert.Contains("not covered by", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_subdomain_of_the_relying_party_is_accepted()
    {
        // The arrangement a real deployment usually has: passkeys registered against
        // the registrable domain so they keep working as hosts come and go.
        new PasskeyOptions
        {
            RelyingPartyId = "example.com",
            Origins = ["https://app.example.com", "https://example.com"],
        }.Validate();
    }

    [Fact]
    public void A_lookalike_domain_is_not_mistaken_for_a_subdomain()
    {
        // notexample.com ends with example.com as a string. Matching on the suffix
        // alone would accept passkeys from a domain an attacker can register.
        var options = new PasskeyOptions
        {
            RelyingPartyId = "example.com",
            Origins = ["https://notexample.com"],
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void No_origins_at_all_is_refused()
    {
        var options = new PasskeyOptions { Origins = [] };

        Assert.Contains("is empty", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_lifetime_that_has_already_passed_is_refused()
    {
        var options = new PasskeyOptions { CeremonyLifetime = TimeSpan.Zero };

        Assert.Contains("must be positive", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
