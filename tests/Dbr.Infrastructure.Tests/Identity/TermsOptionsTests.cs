// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Identity;

namespace Dbr.Infrastructure.Tests.Identity;

/// <summary>
/// The setting that decides what an account is recorded as having agreed to.
/// </summary>
public class TermsOptionsTests
{
    [Fact]
    public void A_version_is_whatever_names_the_text_this_instance_serves()
    {
        // A date here, a semantic version elsewhere, a commit hash somewhere else.
        // Nothing parses it, so nothing should refuse it for its shape.
        new TermsOptions { CurrentVersion = "2026-06-01" }.Validate();
        new TermsOptions { CurrentVersion = "v3.1" }.Validate();
    }

    [Fact]
    public void A_missing_version_stops_startup()
    {
        // Rather than at the first signup, which would be the first person trying to
        // open an account finding out for the operator.
        var options = new TermsOptions();

        Assert.Contains("required", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void The_terms_themselves_are_not_a_version()
    {
        var options = new TermsOptions
        {
            CurrentVersion = new string('x', TermsOptions.MaxVersionLength + 1),
        };

        Assert.Contains("not the terms", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void Whitespace_around_a_version_stops_startup()
    {
        // The one that would otherwise work everywhere except in a browser: a client
        // echoes back what it was shown, and nobody types the trailing space.
        var options = new TermsOptions { CurrentVersion = " 2026-06-01 " };

        Assert.Contains("whitespace", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
