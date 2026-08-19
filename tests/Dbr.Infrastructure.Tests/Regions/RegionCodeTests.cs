// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Regions;

namespace Dbr.Infrastructure.Tests.Regions;

/// <summary>
/// The region code, which two tables and two features have to agree on.
/// </summary>
/// <remarks>
/// Where somebody lives and who a statute protects are matched directly against each
/// other, so this rule holding in one place is what keeps a drift between them from
/// looking like a jurisdiction with no statute.
/// </remarks>
public class RegionCodeTests
{
    [Theory]
    [InlineData("US-CA")]
    [InlineData("EU")]
    [InlineData("US-DC")]
    [InlineData("CA-BC")]
    [InlineData("GB")]
    public void A_coarse_region_is_well_formed(string value) =>
        Assert.True(RegionCode.IsWellFormed(value));

    [Theory]
    [InlineData("California")]
    [InlineData("us-ca")]
    [InlineData("USA")]
    [InlineData("U")]
    [InlineData("US-")]
    [InlineData("US-CALIF")]
    [InlineData("1 Main Street")]
    [InlineData(null)]
    public void Anything_else_is_not(string? value) =>
        Assert.False(RegionCode.IsWellFormed(value));

    [Theory]
    [InlineData("us-ca", "US-CA")]
    [InlineData("  Eu  ", "EU")]
    [InlineData("US-CA", "US-CA")]
    public void Normalizing_produces_what_the_column_stores(string given, string expected) =>
        Assert.Equal(expected, RegionCode.Normalize(given));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_given_normalizes_to_nothing(string? given) =>
        Assert.Null(RegionCode.Normalize(given));

    [Fact]
    public void The_shape_is_checked_after_normalizing_rather_than_before()
    {
        // A lower-cased code is a well-formed code somebody typed casually, not a wrong
        // one. Checking first and normalizing second would refuse it.
        Assert.False(RegionCode.IsWellFormed("us-ca"));
        Assert.True(RegionCode.IsWellFormed(RegionCode.Normalize("us-ca")));
    }
}
