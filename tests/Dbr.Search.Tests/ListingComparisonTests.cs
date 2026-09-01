// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Search.Tests;

/// <summary>
/// Whether a line of text on a listing is the same person, in the three degrees there are.
/// </summary>
/// <remarks>
/// <para>
/// Tested directly rather than only through recorded pages, and there is a reason worth
/// recording. The fixtures are all listings for one person, so every name on them shares a
/// surname — which means the rule that decides most of this, that a different surname is a
/// contradiction, was exercised by nothing. Deleting it from the comparison broke no test at
/// all. This file is what makes it load-bearing.
/// </para>
/// <para>
/// The general shape of that lesson: recorded pages prove the engine reads a real page
/// correctly, and they cannot prove much about the cases a real page happens not to contain.
/// </para>
/// </remarks>
public class ListingComparisonTests
{
    private static readonly IReadOnlyList<string> OnFile = ["Alex Whitfield"];

    private static ProfileAddress Home { get; } = new(
        Guid.NewGuid(),
        "12 Rowan Lane",
        null,
        "Sacramento",
        "CA",
        "95814",
        "US");

    // ------------------------------------------------------------------------- names

    [Theory]
    [InlineData("Alex Whitfield")]
    [InlineData("alex whitfield")]
    [InlineData("  Alex   Whitfield  ")]
    [InlineData("Alex  Whitfield.")]
    public void The_same_name_written_differently_is_the_same_name(string listing)
    {
        Assert.Equal(MatchStrength.Exact, ListingComparison.Names(listing, OnFile));
    }

    /// <summary>
    /// The formatting half of directory listings use.
    /// </summary>
    /// <remarks>
    /// Worth its own test because getting it wrong is silent and expensive: stripping the
    /// comma without reordering leaves the given name sitting where the surname is compared,
    /// so the listing reads as a different person and nobody is ever told about it.
    /// </remarks>
    [Theory]
    [InlineData("Whitfield, Alex", MatchStrength.Exact)]
    [InlineData("Whitfield, Alex J.", MatchStrength.Partial)]
    [InlineData("Thornbury, Alex", MatchStrength.Conflicting)]
    public void A_surname_printed_first_is_still_a_surname(string listing, MatchStrength expected)
    {
        Assert.Equal(expected, ListingComparison.Names(listing, OnFile));
    }

    /// <summary>
    /// And a line with several commas is not read that way.
    /// </summary>
    /// <remarks>
    /// A name with a city appended, or an "also known as" list. Guessing at those would trade
    /// the false negative above for a false positive, which is the worse of the two here:
    /// somebody being shown a stranger's listing as their own data.
    /// </remarks>
    [Fact]
    public void A_line_with_several_commas_is_not_read_as_a_surname_first()
    {
        Assert.Equal(
            MatchStrength.Conflicting,
            ListingComparison.Names("Whitfield, Alex, Sacramento", OnFile));
    }

    /// <summary>
    /// The rule that decides most listings, and the one no recorded page exercised.
    /// </summary>
    [Theory]
    [InlineData("Alex Thornbury")]
    [InlineData("Alex Whitford")]
    [InlineData("Whitfield Alexander")]
    public void A_different_surname_is_somebody_else(string listing)
    {
        Assert.Equal(MatchStrength.Conflicting, ListingComparison.Names(listing, OnFile));
    }

    [Theory]
    [InlineData("Alex J. Whitfield")]
    [InlineData("Alexandra Whitfield")]
    [InlineData("A Whitfield")]
    public void A_shortened_or_extended_given_name_with_the_same_surname_is_partial(string listing)
    {
        Assert.Equal(MatchStrength.Partial, ListingComparison.Names(listing, OnFile));
    }

    [Fact]
    public void A_different_given_name_with_the_same_surname_is_somebody_else()
    {
        // Siblings share a surname, and the whole point of the group is telling people apart.
        Assert.Equal(MatchStrength.Conflicting, ListingComparison.Names("Marian Whitfield", OnFile));
    }

    /// <summary>Any of the names on file agreeing is agreement.</summary>
    /// <remarks>
    /// A profile carries every spelling somebody is listed under — a maiden name, a
    /// transliteration — and taking the strongest reading is what stops one of them turning a
    /// match into a contradiction simply by also being in the list.
    /// </remarks>
    [Fact]
    public void A_listing_matching_any_name_on_file_matches()
    {
        var several = new[] { "Alex Whitfield", "Alex Bramley" };

        Assert.Equal(MatchStrength.Exact, ListingComparison.Names("Alex Bramley", several));
        Assert.Equal(MatchStrength.Exact, ListingComparison.Names("Alex Whitfield", several));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_listing_that_printed_no_name_says_nothing(string listing)
    {
        // Absent rather than contradicted: a page that does not print a name disagrees with
        // nothing, and reporting a contradiction would make an uninformative page look like
        // evidence that this is somebody else.
        Assert.Null(ListingComparison.Names(listing, OnFile));
    }

    [Fact]
    public void A_profile_with_no_names_cannot_be_matched_on_one()
    {
        Assert.Null(ListingComparison.Names("Alex Whitfield", []));
    }

    // --------------------------------------------------------------------- addresses

    [Fact]
    public void The_same_street_and_town_is_the_same_address()
    {
        Assert.Equal(
            MatchStrength.Exact,
            ListingComparison.Addresses("12 Rowan Lane, Sacramento, CA 95814", [Home]));
    }

    [Fact]
    public void The_same_town_at_a_different_street_is_only_partial()
    {
        // The coincidence a city of half a million people produces constantly.
        Assert.Equal(
            MatchStrength.Partial,
            ListingComparison.Addresses("4409 Bellhaven Court, Sacramento, CA 95826", [Home]));
    }

    [Fact]
    public void The_same_street_in_a_different_town_is_still_worth_something()
    {
        Assert.Equal(
            MatchStrength.Partial,
            ListingComparison.Addresses("12 Rowan Lane, Davis, CA 95616", [Home]));
    }

    [Fact]
    public void An_address_agreeing_with_nothing_on_file_is_a_contradiction()
    {
        Assert.Equal(
            MatchStrength.Conflicting,
            ListingComparison.Addresses("88 Cedar Street, Bangor, ME 04401", [Home]));
    }

    /// <summary>An old address on file is still an address on file.</summary>
    [Fact]
    public void A_listing_matching_a_former_address_matches()
    {
        var former = Home with { Line1 = "318 Wexford Avenue", City = "Davis", PostalCode = "95616" };

        Assert.Equal(
            MatchStrength.Exact,
            ListingComparison.Addresses("318 Wexford Avenue, Davis, CA 95616", [Home, former]));
    }

    // ---------------------------------------------------------------------- contacts

    [Fact]
    public void The_same_mailbox_is_the_same_person()
    {
        var email = new ProfileContact(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test");

        Assert.Equal(
            MatchStrength.Exact,
            ListingComparison.Contacts("ALEX@example.test", [email]));
    }

    [Fact]
    public void A_different_mailbox_is_a_contradiction_rather_than_a_weak_match()
    {
        var email = new ProfileContact(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test");

        // There is no partial version of an email address, and inventing one — a shared
        // domain, say — would score a coincidence as evidence at the weight this system gives
        // its most identifying group.
        Assert.Equal(
            MatchStrength.Conflicting,
            ListingComparison.Contacts("someone.else@example.test", [email]));
    }

    [Theory]
    [InlineData("(916) 555-0142")]
    [InlineData("916-555-0142")]
    [InlineData("916.555.0142")]
    public void One_telephone_number_written_several_ways_is_one_number(string listing)
    {
        var phone = new ProfileContact(Guid.NewGuid(), ProfileContactKind.Phone, "9165550142");

        Assert.Equal(MatchStrength.Exact, ListingComparison.Contacts(listing, [phone]));
    }

    [Fact]
    public void A_handful_of_digits_is_not_a_telephone_number()
    {
        var phone = new ProfileContact(Guid.NewGuid(), ProfileContactKind.Phone, "9165550142");

        // A house number or an age, read out of a listing that printed one where a number was
        // expected. Comparing it as a phone number would be comparing nothing to something.
        Assert.Equal(MatchStrength.Conflicting, ListingComparison.Contacts("Age 41", [phone]));
    }
}
