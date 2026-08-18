// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Consent;

namespace Dbr.Infrastructure.Tests.Consent;

/// <summary>
/// The setting that decides what a grant or a revocation is recorded against.
/// </summary>
public class ConsentPolicyOptionsTests
{
    [Fact]
    public void A_version_is_whatever_names_the_text_this_instance_serves()
    {
        // A date here, a semantic version elsewhere, a commit hash somewhere else.
        // Nothing parses it, so nothing should refuse it for its shape.
        new ConsentPolicyOptions { PolicyVersion = "2026-06-01" }.Validate();
        new ConsentPolicyOptions { PolicyVersion = "v3.1" }.Validate();
    }

    [Fact]
    public void A_missing_version_stops_startup()
    {
        // Rather than at the first decision, which would be somebody flipping a switch
        // and finding out for the operator.
        var options = new ConsentPolicyOptions();

        Assert.Contains("required", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void The_policy_itself_is_not_a_version()
    {
        var options = new ConsentPolicyOptions
        {
            PolicyVersion = new string('x', ConsentPolicyOptions.MaxVersionLength + 1),
        };

        Assert.Contains("not the policy", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void Whitespace_around_a_version_stops_startup()
    {
        // The one that would otherwise work everywhere except in a browser: a client
        // echoes back what it was shown, and nobody types the trailing space.
        var options = new ConsentPolicyOptions { PolicyVersion = " 2026-06-01 " };

        Assert.Contains("whitespace", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
