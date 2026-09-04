// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;
using Dbr.Domain.Catalog;
using Dbr.Domain.Removals;

namespace Dbr.Api.Tests;

/// <summary>
/// What the removal routes accept before anything touches the database.
/// </summary>
/// <remarks>
/// The interesting one is the kind of demand. It is required and undefaulted, because the
/// value that would read as an obvious default is also the broadest and least reversible of
/// the three — a client that forgot the field would be telling a company to erase somebody
/// rather than to stop selling their data.
/// </remarks>
public class RemovalRequestValidationTests
{
    private static OpenRemovalRequest Request(
        Guid? brokerId = null,
        string? requestType = "delete",
        Guid? profileId = null,
        Guid? exposureId = null) =>
        new(brokerId ?? Guid.NewGuid(), requestType, profileId, exposureId);

    [Fact]
    public void A_demand_naming_a_company_and_a_right_is_accepted()
    {
        var validation = RemovalRequestValidation.Validate(Request());

        Assert.Null(validation.Problem);
        Assert.Equal(LegalRequestType.Delete, validation.RequestType);
    }

    [Theory]
    [InlineData("delete", LegalRequestType.Delete)]
    [InlineData("opt_out_sale", LegalRequestType.OptOutSale)]
    [InlineData("opt_out_targeted_ads", LegalRequestType.OptOutTargetedAds)]
    public void Every_right_the_catalog_keys_on_can_be_asked_for(string wire, LegalRequestType expected)
    {
        var validation = RemovalRequestValidation.Validate(Request(requestType: wire));

        Assert.Null(validation.Problem);
        Assert.Equal(expected, validation.RequestType);
    }

    [Fact]
    public void A_demand_addressed_to_nobody_is_refused()
    {
        Assert.NotNull(RemovalRequestValidation.Validate(Request(brokerId: Guid.Empty)).Problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_demand_that_does_not_say_which_right_is_refused(string? requestType)
    {
        var validation = RemovalRequestValidation.Validate(Request(requestType: requestType));

        Assert.NotNull(validation.Problem);
    }

    /// <summary>
    /// An unrecognised right is refused rather than resolved to the nearest thing.
    /// </summary>
    /// <remarks>
    /// A typo becoming a deletion is the failure worth spending an error message on.
    /// </remarks>
    [Theory]
    [InlineData("Delete")]
    [InlineData("erase")]
    [InlineData("opt-out-sale")]
    public void A_right_this_service_does_not_know_is_refused(string requestType)
    {
        Assert.NotNull(RemovalRequestValidation.Validate(Request(requestType: requestType)).Problem);
    }

    [Fact]
    public void An_empty_profile_id_is_refused_rather_than_read_as_absent()
    {
        // Absent means the tenant's own identity, which is a real and common request. An
        // all-zero id is what a client sends when it meant to send nothing, and the two
        // should not be the same thing.
        Assert.NotNull(RemovalRequestValidation.Validate(Request(profileId: Guid.Empty)).Problem);
    }

    [Fact]
    public void An_empty_exposure_id_is_refused_rather_than_read_as_absent()
    {
        Assert.NotNull(RemovalRequestValidation.Validate(Request(exposureId: Guid.Empty)).Problem);
    }

    [Fact]
    public void A_demand_citing_no_listing_is_complete()
    {
        // The case the schema was widened for: nothing about the right depends on having
        // found a page first.
        Assert.Null(RemovalRequestValidation.Validate(Request(exposureId: null)).Problem);
    }

    [Fact]
    public void No_filter_asks_for_everything()
    {
        var parsed = RemovalFilters.Parse(null, null);

        Assert.Null(parsed.Problem);
        Assert.Null(parsed.Filter.Status);
        Assert.Null(parsed.Filter.ProfileId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_parameter_is_absent_rather_than_invalid(string value)
    {
        // What a client sends when a form control has nothing selected.
        var parsed = RemovalFilters.Parse(value, value);

        Assert.Null(parsed.Problem);
        Assert.Null(parsed.Filter.Status);
        Assert.Null(parsed.Filter.ProfileId);
    }

    [Fact]
    public void Every_status_a_demand_can_be_in_can_be_filtered_for()
    {
        foreach (var status in Enum.GetValues<RemovalRequestStatus>())
        {
            var parsed = RemovalFilters.Parse(RemovalVocabulary.ToWire(status), null);

            Assert.Null(parsed.Problem);
            Assert.Equal(status, parsed.Filter.Status);
        }
    }

    /// <summary>
    /// An unrecognised status is refused, not dropped and not matched against nothing.
    /// </summary>
    /// <remarks>
    /// Dropped, it answers a different question while looking like a complete list. Matched
    /// against nothing, it says nothing has been demanded on somebody's behalf — which is a
    /// sentence they would act on, produced from a typo.
    /// </remarks>
    [Theory]
    [InlineData("Queued")]
    [InlineData("in_flight")]
    [InlineData("awaiting")]
    public void A_status_this_service_does_not_know_is_refused(string status)
    {
        Assert.NotNull(RemovalFilters.Parse(status, null).Problem);
    }

    [Fact]
    public void A_profile_filter_that_is_not_an_id_is_refused()
    {
        Assert.NotNull(RemovalFilters.Parse(null, "my-own-profile").Problem);
    }

    [Fact]
    public void A_profile_filter_that_is_an_id_is_read()
    {
        var profileId = Guid.NewGuid();
        var parsed = RemovalFilters.Parse(null, profileId.ToString());

        Assert.Null(parsed.Problem);
        Assert.Equal(profileId, parsed.Filter.ProfileId);
    }
}
