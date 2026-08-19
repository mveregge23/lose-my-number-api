// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;
using Dbr.Domain.Catalog;

namespace Dbr.Api.Tests;

/// <summary>
/// What the catalog routes accept in a query string, and what they refuse.
/// </summary>
/// <remarks>
/// The interesting cases are all about the difference between an absent filter and a
/// wrong one. Both leave a client holding a plausible list, so the code has to tell them
/// apart before the query is built rather than after.
/// </remarks>
public class CatalogFilterTests
{
    [Theory]
    [InlineData("webform", RemovalMethod.WebForm)]
    [InlineData("email", RemovalMethod.Email)]
    [InlineData("api", RemovalMethod.Api)]
    [InlineData("postal", RemovalMethod.Postal)]
    public void A_removal_method_is_read_as_the_column_spells_it(string wire, RemovalMethod expected)
    {
        var parsed = CatalogFilters.ParseBrokerFilter(wire, null);

        Assert.Null(parsed.Problem);
        Assert.Equal(expected, parsed.Filter.RemovalMethod);
    }

    [Theory]
    [InlineData("WebForm")]
    [InlineData("WEBFORM")]
    [InlineData("web_form")]
    public void A_removal_method_spelled_some_other_way_is_refused(string wire)
    {
        // One vocabulary for the column, the conversion and the wire. Accepting a second
        // spelling here would make the value a client reads back differ from the value it
        // may send, which is the kind of thing that works until somebody round-trips it.
        var parsed = CatalogFilters.ParseBrokerFilter(wire, null);

        Assert.NotNull(parsed.Problem);
    }

    [Fact]
    public void A_legal_basis_filter_takes_an_id_and_says_so_when_it_gets_a_code()
    {
        // 'CCPA' is the obvious guess and it is wrong: a code is not unique on its own,
        // since one regime grants different demands on different terms. Saying which is
        // meant beats an empty list.
        var parsed = CatalogFilters.ParseBrokerFilter(null, "CCPA");

        Assert.NotNull(parsed.Problem);
        Assert.Contains("id", parsed.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_broker_filters_survive_being_given_at_once()
    {
        var id = Guid.NewGuid();

        var parsed = CatalogFilters.ParseBrokerFilter("postal", id.ToString());

        Assert.Null(parsed.Problem);
        Assert.Equal(RemovalMethod.Postal, parsed.Filter.RemovalMethod);
        Assert.Equal(id, parsed.Filter.LegalBasisId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_filter_nobody_set_is_absent_rather_than_wrong(string? value)
    {
        var brokers = CatalogFilters.ParseBrokerFilter(value, value);

        Assert.Null(brokers.Problem);
        Assert.Null(brokers.Filter.RemovalMethod);
        Assert.Null(brokers.Filter.LegalBasisId);

        var bases = CatalogFilters.ParseLegalBasisFilter(value, value);

        Assert.Null(bases.Problem);
        Assert.Null(bases.Filter.ResidencyScope);
        Assert.Null(bases.Filter.RequestType);
    }

    [Theory]
    [InlineData("us-ca", "US-CA")]
    [InlineData("  US-CA  ", "US-CA")]
    [InlineData("eu", "EU")]
    public void A_residency_scope_is_normalized_to_what_the_column_holds(string given, string expected)
    {
        // The comparison against a stored scope is exact, so normalizing is the whole of
        // whether a lower-cased query finds anything.
        var parsed = CatalogFilters.ParseLegalBasisFilter(given, null);

        Assert.Null(parsed.Problem);
        Assert.Equal(expected, parsed.Filter.ResidencyScope);
    }

    [Theory]
    [InlineData("California")]
    [InlineData("US-CALIF")]
    [InlineData("1 Main Street, Springfield")]
    public void A_residency_scope_that_is_not_a_region_code_is_refused(string given)
    {
        // The same rule the column enforces and a profile's region is held to. Refusing
        // it here is what turns an empty list into an answer naming the shape meant.
        var parsed = CatalogFilters.ParseLegalBasisFilter(given, null);

        Assert.NotNull(parsed.Problem);
    }

    [Theory]
    [InlineData("delete", LegalRequestType.Delete)]
    [InlineData("opt_out_sale", LegalRequestType.OptOutSale)]
    [InlineData("opt_out_targeted_ads", LegalRequestType.OptOutTargetedAds)]
    public void A_request_type_is_read_as_the_column_spells_it(string wire, LegalRequestType expected)
    {
        var parsed = CatalogFilters.ParseLegalBasisFilter(null, wire);

        Assert.Null(parsed.Problem);
        Assert.Equal(expected, parsed.Filter.RequestType);
    }

    [Theory]
    [InlineData("OptOutSale")]
    [InlineData("optoutsale")]
    [InlineData("opt-out-sale")]
    public void A_request_type_spelled_some_other_way_is_refused(string wire)
    {
        var parsed = CatalogFilters.ParseLegalBasisFilter(null, wire);

        Assert.NotNull(parsed.Problem);
    }
}
