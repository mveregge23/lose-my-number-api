// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;
using Dbr.Domain.Monitoring;

namespace Dbr.Api.Tests;

/// <summary>
/// What the exposure list accepts as a filter, and what it refuses.
/// </summary>
public class ExposureFilterTests
{
    [Fact]
    public void No_filters_asks_for_everything()
    {
        var parsed = ExposureFilters.Parse(null, null);

        Assert.Null(parsed.Problem);
        Assert.Null(parsed.Filter.Status);
        Assert.Null(parsed.Filter.BrokerId);
    }

    [Theory]
    [InlineData("new", ExposureStatus.New)]
    [InlineData("requested", ExposureStatus.Requested)]
    [InlineData("removed", ExposureStatus.Removed)]
    [InlineData("reappeared", ExposureStatus.Reappeared)]
    [InlineData("dismissed", ExposureStatus.Dismissed)]
    public void Every_status_a_client_is_handed_can_be_sent_back(string wire, ExposureStatus expected)
    {
        // The values in the JSON and the values a filter takes are the same values. A
        // client filtering by a status it was just shown must not have to translate it.
        var parsed = ExposureFilters.Parse(wire, null);

        Assert.Null(parsed.Problem);
        Assert.Equal(expected, parsed.Filter.Status);
    }

    [Fact]
    public void An_unknown_status_is_refused_rather_than_dropped()
    {
        // The failure this prevents is the worst kind of quiet one. Dropped, the filter
        // answers a different question and looks complete; matched against nothing, it
        // says somebody is not listed anywhere. Both are sentences people act on.
        var parsed = ExposureFilters.Parse("pending", null);

        Assert.NotNull(parsed.Problem);
        Assert.Contains("'new'", parsed.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_is_matched_exactly_rather_than_loosely()
    {
        // Enum.TryParse would take "New", "NEW" and even "0" here. The column is indexed
        // on one spelling and the check constraint accepts one spelling, so anything
        // looser would let a filter succeed against a value the database has never held.
        Assert.NotNull(ExposureFilters.Parse("New", null).Problem);
        Assert.NotNull(ExposureFilters.Parse("0", null).Problem);
    }

    [Fact]
    public void Surrounding_space_is_forgiven()
    {
        var parsed = ExposureFilters.Parse("  removed  ", null);

        Assert.Null(parsed.Problem);
        Assert.Equal(ExposureStatus.Removed, parsed.Filter.Status);
    }

    [Fact]
    public void An_empty_parameter_is_absent_rather_than_invalid()
    {
        // What a client sends when a form control has nothing selected. Refusing it would
        // make every such client special-case its own query string.
        var parsed = ExposureFilters.Parse(string.Empty, "   ");

        Assert.Null(parsed.Problem);
        Assert.Null(parsed.Filter.Status);
        Assert.Null(parsed.Filter.BrokerId);
    }

    [Fact]
    public void A_broker_that_is_not_an_id_is_refused_with_the_reason()
    {
        // Naming a broker by domain is the plausible mistake, so the message says where
        // the id comes from rather than only that this one was malformed.
        var parsed = ExposureFilters.Parse(null, "acme.test");

        Assert.NotNull(parsed.Problem);
        Assert.Contains("/api/v1/brokers", parsed.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_filters_combine()
    {
        var brokerId = Guid.NewGuid();
        var parsed = ExposureFilters.Parse("new", brokerId.ToString());

        Assert.Null(parsed.Problem);
        Assert.Equal(ExposureStatus.New, parsed.Filter.Status);
        Assert.Equal(brokerId, parsed.Filter.BrokerId);
    }
}
