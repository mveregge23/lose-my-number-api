// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;
using Dbr.Domain.Profiles;

namespace Dbr.Api.Tests;

/// <summary>
/// The only layer that can refuse anything a profile is asked to hold.
/// </summary>
/// <remarks>
/// Identity fields are encrypted blobs, so there is no column length, no check
/// constraint and no unique index underneath them — the database cannot see a name, let
/// alone judge one. Every rule about what a profile may contain is either here or
/// nowhere, which is why these are worth testing one by one rather than through the
/// endpoints.
/// </remarks>
public class ProfileRequestValidationTests
{
    [Fact]
    public void An_empty_profile_is_allowed()
    {
        // Signup creates one with nothing in it, so "empty" has to be a state the rules
        // permit rather than an error a new account meets immediately.
        Assert.Null(ProfileRequestValidation.Validate(new ReplaceProfileRequest(null, null, null, null)));
    }

    [Fact]
    public void A_blank_name_is_refused_rather_than_stored()
    {
        var problem = ProfileRequestValidation.Validate(
            new ReplaceProfileRequest(["Alex Whitfield", "   "], null, null, null));

        Assert.NotNull(problem);
    }

    [Fact]
    public void More_names_than_a_person_has_are_refused()
    {
        var names = Enumerable.Range(0, ProfileLimits.MaxNames + 1).Select(i => $"Name {i}").ToList();

        Assert.NotNull(ProfileRequestValidation.Validate(new ReplaceProfileRequest(names, null, null, null)));
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        var name = new string('a', ProfileLimits.MaxNameLength + 1);

        Assert.NotNull(ProfileRequestValidation.Validate(new ReplaceProfileRequest([name], null, null, null)));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("EMAIL")]
    [InlineData("phone")]
    public void A_contact_kind_is_read_however_it_is_capitalised(string kind)
    {
        var request = new ReplaceProfileRequest(
            null,
            null,
            [new ProfileContactRequest(kind, "alex@example.test")],
            null);

        Assert.Null(ProfileRequestValidation.Validate(request));
    }

    [Theory]
    [InlineData("fax")]
    [InlineData("")]
    [InlineData(null)]
    public void A_contact_kind_this_system_has_no_meaning_for_is_refused(string? kind)
    {
        var request = new ReplaceProfileRequest(
            null,
            null,
            [new ProfileContactRequest(kind, "alex@example.test")],
            null);

        Assert.NotNull(ProfileRequestValidation.Validate(request));
    }

    [Fact]
    public void A_date_of_birth_in_the_future_is_refused()
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        Assert.NotNull(ProfileRequestValidation.Validate(new ReplaceProfileRequest(null, tomorrow, null, null)));
    }

    [Fact]
    public void Todays_date_is_not_refused_as_a_future_one()
    {
        // The boundary, because an off-by-one here refuses a real answer rather than a
        // wrong one, and nobody would think to check.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Null(ProfileRequestValidation.Validate(new ReplaceProfileRequest(null, today, null, null)));
    }

    [Fact]
    public void A_date_of_birth_before_anybody_alive_is_refused()
    {
        var early = ProfileLimits.EarliestDateOfBirth.AddDays(-1);

        Assert.NotNull(ProfileRequestValidation.Validate(new ReplaceProfileRequest(null, early, null, null)));
    }

    [Theory]
    [InlineData("US-CA")]
    [InlineData("us-ca")]
    [InlineData("EU")]
    [InlineData("US-DC")]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_coarse_region_is_accepted(string? region)
    {
        Assert.Null(ProfileRequestValidation.Validate(new ReplaceProfileRequest(null, null, null, region)));
    }

    [Theory]
    [InlineData("12 Rowan Lane, Sacramento")]
    [InlineData("United States")]
    [InlineData("U")]
    [InlineData("US-CALIFORNIA")]
    public void Anything_more_precise_than_a_region_code_is_refused(string region)
    {
        // This is the one field of a profile that is not encrypted, because jurisdiction
        // has to be resolved on every removal request without a decryption. Keeping it
        // coarse is what makes that safe, so the shape is enforced rather than trusted.
        Assert.NotNull(ProfileRequestValidation.Validate(new ReplaceProfileRequest(null, null, null, region)));
    }

    [Fact]
    public void A_region_is_stored_upper_cased()
    {
        var region = ProfileRequestValidation.ToResidencyRegion(
            new ReplaceProfileRequest(null, null, null, " us-ca "));

        Assert.Equal("US-CA", region);
    }

    [Fact]
    public void A_blank_region_is_stored_as_nothing_at_all()
    {
        // Rather than an empty string, which the database would reject and which would
        // otherwise mean "somewhere with a two-letter name".
        Assert.Null(ProfileRequestValidation.ToResidencyRegion(new ReplaceProfileRequest(null, null, null, "   ")));
    }

    [Fact]
    public void Names_and_contacts_are_trimmed_on_the_way_in()
    {
        var details = ProfileRequestValidation.ToDetails(new ReplaceProfileRequest(
            ["  Alex Whitfield  "],
            null,
            [new ProfileContactRequest("Email", "  alex@example.test  ")],
            null));

        Assert.Equal(["Alex Whitfield"], details.Names);
        Assert.Equal("alex@example.test", details.Contacts.Single().Value);
        Assert.Equal(ProfileContactKind.Email, details.Contacts.Single().Kind);
    }

    [Fact]
    public void An_address_needs_a_street_a_city_and_a_country()
    {
        Assert.NotNull(ProfileRequestValidation.Validate(
            new AddAddressRequest(null, null, "Sacramento", "CA", "95814", "US")));

        Assert.NotNull(ProfileRequestValidation.Validate(
            new AddAddressRequest("12 Rowan Lane", null, "  ", "CA", "95814", "US")));

        Assert.NotNull(ProfileRequestValidation.Validate(
            new AddAddressRequest("12 Rowan Lane", null, "Sacramento", "CA", "95814", null)));
    }

    [Theory]
    [InlineData("United States")]
    [InlineData("USA")]
    [InlineData("U")]
    public void A_country_that_is_not_a_two_letter_code_is_refused(string country)
    {
        // The legal-basis catalog resolves applicability by comparing codes. "USA" and
        // "United States" name the same place and match neither each other nor "US".
        Assert.NotNull(ProfileRequestValidation.Validate(
            new AddAddressRequest("12 Rowan Lane", null, "Sacramento", "CA", "95814", country)));
    }

    [Fact]
    public void An_address_with_no_region_or_postal_code_is_allowed()
    {
        // Plenty of addresses have neither, and refusing them would make this unusable
        // outside the handful of countries that do.
        var request = new AddAddressRequest("12 Rowan Lane", null, "Sacramento", null, null, "us");

        Assert.Null(ProfileRequestValidation.Validate(request));

        var address = ProfileRequestValidation.ToAddress(request);

        Assert.Null(address.Region);
        Assert.Null(address.PostalCode);
        Assert.Equal("US", address.Country);
    }

    [Fact]
    public void An_address_field_longer_than_the_limit_is_refused()
    {
        var long1 = new string('a', ProfileLimits.MaxAddressLineLength + 1);

        Assert.NotNull(ProfileRequestValidation.Validate(
            new AddAddressRequest(long1, null, "Sacramento", "CA", "95814", "US")));

        Assert.NotNull(ProfileRequestValidation.Validate(
            new AddAddressRequest("12 Rowan Lane", null, new string('a', ProfileLimits.MaxCityLength + 1), null, null, "US")));
    }

    [Fact]
    public void The_id_of_an_address_is_not_something_a_request_can_carry()
    {
        // Stated as a test because it is a property of the type rather than of a rule:
        // these ids live inside an encrypted column with no unique index behind them, so
        // one chosen anywhere but the storage layer could silently already be in use.
        Assert.DoesNotContain(
            typeof(AddAddressRequest).GetProperties(),
            property => property.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
    }
}
