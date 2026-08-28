// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Dbr.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;

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

    /// <summary>
    /// The value the compose file actually sets, bound the way the API binds it.
    /// </summary>
    /// <remarks>
    /// A setting is only as good as its spelling in the file an operator edits. Everything
    /// else here constructs the options directly and would pass just as well if
    /// <c>00:05:00</c> bound to nothing at all &mdash; and the failure that would cause is
    /// the worst kind, an API that will not start, discovered by whoever ran
    /// <c>docker compose up</c> rather than by anybody who could have prevented it.
    /// </remarks>
    [Theory]
    [InlineData("00:05:00", 5)]
    [InlineData("00:00:30", 0)]
    [InlineData("01:00:00", 60)]
    public void The_lifetime_binds_from_the_string_an_operator_writes(string configured, int minutes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{IdentityReleaseOptions.SectionName}:Lifetime"] = configured,
            })
            .Build();

        var options = new IdentityReleaseOptions();
        configuration.GetSection(IdentityReleaseOptions.SectionName).Bind(options);

        Assert.Equal(TimeSpan.Parse(configured, CultureInfo.InvariantCulture), options.Lifetime);
        Assert.Equal(minutes, (int)options.Lifetime.TotalMinutes);

        options.Validate();
    }
}
