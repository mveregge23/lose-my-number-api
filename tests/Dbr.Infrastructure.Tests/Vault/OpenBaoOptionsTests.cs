// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Vault;

namespace Dbr.Infrastructure.Tests.Vault;

/// <summary>
/// Settings for reaching the key manager.
/// </summary>
/// <remarks>
/// Worth failing at startup over. The alternative is finding out at the moment
/// somebody's identity is being written, with the write half-finished and the reason
/// buried in an HTTP failure.
/// </remarks>
public class OpenBaoOptionsTests
{
    private static OpenBaoOptions Valid() => new()
    {
        Address = "http://openbao:8200",
        Token = "a-token",
    };

    [Fact]
    public void A_complete_configuration_is_accepted()
    {
        Valid().Validate();
    }

    [Fact]
    public void A_host_without_a_scheme_is_refused()
    {
        // The likeliest mistake, and one that produces a confusing failure much later.
        var options = Valid();
        options.Address = "openbao:8200";

        Assert.Contains("absolute http or https URL", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_scheme_that_is_not_http_is_refused()
    {
        var options = Valid();
        options.Address = "ftp://openbao:8200";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_missing_token_is_refused()
    {
        var options = Valid();
        options.Token = "";

        Assert.Contains("is required", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void A_blank_transit_mount_is_refused()
    {
        var options = Valid();
        options.TransitMount = " ";

        Assert.Contains("mounted at", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
