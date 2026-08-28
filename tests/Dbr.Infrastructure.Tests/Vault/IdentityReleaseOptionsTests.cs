// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later


using Dbr.Infrastructure.Vault;

namespace Dbr.Infrastructure.Tests.Vault;

/// <summary>
/// The one setting a grant has, and the two ways of getting it wrong.
/// </summary>
/// <remarks>
/// Both failures are quiet ones, which is why they are refused at startup. A
/// non-positive lifetime mints grants that have already expired, so every redemption is
/// refused and it reads as a broken worker rather than a setting. An over-long one is
/// standing access to an identity wearing the shape of a scoped release, and nothing about
/// running the system would look different.
/// </remarks>
public class IdentityReleaseOptionsTests
{
    [Fact]
    public void The_default_is_the_five_minutes_the_design_names()
    {
        var options = new IdentityReleaseOptions();

        options.Validate();

        Assert.Equal(TimeSpan.FromMinutes(5), options.Lifetime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_lifetime_that_has_already_run_out_is_refused(int minutes)
    {
        var options = new IdentityReleaseOptions { Lifetime = TimeSpan.FromMinutes(minutes) };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lifetime_past_the_ceiling_is_refused()
    {
        var options = new IdentityReleaseOptions
        {
            Lifetime = IdentityReleaseOptions.MaxLifetime + TimeSpan.FromMinutes(1),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void The_ceiling_itself_is_allowed()
    {
        var options = new IdentityReleaseOptions { Lifetime = IdentityReleaseOptions.MaxLifetime };

        options.Validate();
    }
}
