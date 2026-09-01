// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Search.Tests;

/// <summary>
/// What a recipe may write into a query, and what it causes to be decrypted by writing it.
/// </summary>
/// <remarks>
/// The second half is why this file matters more than it looks. The groups a recipe names are
/// the groups its grant covers, so a placeholder quietly mapping to the wrong one would mean a
/// release wider than the document — the exact failure the whole recipe tier is arranged to
/// make impossible.
/// </remarks>
public class RecipeTemplateTests
{
    private static readonly ProfileIdentityFields Alex = new(
        ["Alex Whitfield"],
        [new ProfileAddress(Guid.NewGuid(), "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US")],
        [new ProfileContact(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test")],
        new DateOnly(1985, 4, 17));

    private static RecipeTemplate Parse(string raw)
    {
        var template = RecipeTemplate.TryParse(raw, out var problem);

        Assert.Null(problem);
        Assert.NotNull(template);

        return template;
    }

    [Theory]
    [InlineData("names.full", IdentityField.Names)]
    [InlineData("names.first", IdentityField.Names)]
    [InlineData("names.last", IdentityField.Names)]
    [InlineData("addresses.first.line1", IdentityField.Addresses)]
    [InlineData("addresses.first.city", IdentityField.Addresses)]
    [InlineData("addresses.first.region", IdentityField.Addresses)]
    [InlineData("addresses.first.postalCode", IdentityField.Addresses)]
    [InlineData("contacts.email", IdentityField.Contacts)]
    [InlineData("contacts.phone", IdentityField.Contacts)]
    [InlineData("dateOfBirth.year", IdentityField.DateOfBirth)]
    public void Writing_a_placeholder_asks_for_the_group_it_reads(string path, IdentityField field)
    {
        var template = Parse($"/s?q={{{{{path}}}}}");

        Assert.Equal([field], template.RequiredFields);
    }

    /// <summary>
    /// The structural guarantee, stated as a test.
    /// </summary>
    /// <remarks>
    /// A query that names a name and a city cannot cause a date of birth to be decrypted —
    /// not because nothing asks for one at the wrong moment, but because there is no moment
    /// at which it could.
    /// </remarks>
    [Fact]
    public void A_query_that_mentions_no_date_of_birth_cannot_release_one()
    {
        var template = Parse("/search?name={{names.full}}&city={{addresses.first.city}}");

        Assert.Equal([IdentityField.Names, IdentityField.Addresses], template.RequiredFields.Order().ToArray());
        Assert.DoesNotContain(IdentityField.DateOfBirth, template.RequiredFields);
        Assert.DoesNotContain(IdentityField.Contacts, template.RequiredFields);
    }

    [Fact]
    public void A_placeholder_this_build_has_no_meaning_for_is_refused()
    {
        var template = RecipeTemplate.TryParse("/s?q={{names.middle}}", out var problem);

        Assert.Null(template);
        Assert.NotNull(problem);

        // The refusal lists what is available, because the person reading it is writing a
        // recipe and the next thing they need is the vocabulary.
        Assert.Contains("names.full", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one that would be a vulnerability rather than a bug.
    /// </summary>
    /// <remarks>
    /// A recipe is reviewed as data on the grounds that there is nothing in it to execute. A
    /// placeholder that could walk an object graph would make reviewing one mean reasoning
    /// about what that walk can reach, which is the code bar arrived at by accident.
    /// </remarks>
    [Theory]
    [InlineData("{{ }}")]
    [InlineData("{{tenant.id}}")]
    [InlineData("{{identity}}")]
    [InlineData("{{names}}")]
    [InlineData("{{Names.Full}}")]
    public void Nothing_outside_the_vocabulary_is_a_placeholder(string raw)
    {
        Assert.Null(RecipeTemplate.TryParse($"/s?q={raw}", out _));
    }

    [Fact]
    public void A_query_with_no_placeholders_reads_nothing()
    {
        Assert.Empty(Parse("/search?everyone=true").RequiredFields);
    }

    [Fact]
    public void The_identity_is_written_where_the_placeholders_are()
    {
        var rendered = Parse("/search?name={{names.full}}&city={{addresses.first.city}}")
            .Render(Alex);

        Assert.Null(rendered.Missing);
        Assert.Equal("/search?name=Alex%20Whitfield&city=Sacramento", rendered.Value);
    }

    /// <summary>
    /// Values are escaped and the recipe's own punctuation is not.
    /// </summary>
    /// <remarks>
    /// A name containing an ampersand would otherwise end one query parameter and begin
    /// another — a bug that appears only for the people whose names contain one, which is the
    /// worst kind to find in production.
    /// </remarks>
    [Fact]
    public void A_name_cannot_end_a_query_parameter_early()
    {
        var identity = Alex with { Names = ["Ampersand & Co"] };

        var rendered = Parse("/search?name={{names.full}}&page=1").Render(identity);

        Assert.Equal("/search?name=Ampersand%20%26%20Co&page=1", rendered.Value);
    }

    [Theory]
    [InlineData("names.first", "Alex")]
    [InlineData("names.last", "Whitfield")]
    [InlineData("addresses.first.line1", "12%20Rowan%20Lane")]
    [InlineData("addresses.first.region", "CA")]
    [InlineData("addresses.first.postalCode", "95814")]
    [InlineData("contacts.email", "alex%40example.test")]
    [InlineData("dateOfBirth.year", "1985")]
    public void Each_placeholder_writes_what_it_says_it_does(string path, string expected)
    {
        Assert.Equal(expected, Parse($"{{{{{path}}}}}").Render(Alex).Value);
    }

    /// <summary>
    /// A profile with nothing where the query needs something.
    /// </summary>
    /// <remarks>
    /// Reported rather than rendered as an empty parameter. A search for a person with no
    /// city, sent as <c>city=</c>, is a request the company answers — usually with everybody
    /// of that name in the country, which is a great many listings about people who are not
    /// this person.
    /// </remarks>
    [Fact]
    public void A_query_that_needs_something_the_profile_has_not_got_says_so()
    {
        var noAddress = Alex with { Addresses = [] };

        var rendered = Parse("/search?city={{addresses.first.city}}").Render(noAddress);

        Assert.Null(rendered.Value);
        Assert.Equal("addresses.first.city", rendered.Missing);
    }

    [Fact]
    public void A_contact_of_the_wrong_kind_is_not_a_substitute_for_the_one_asked_for()
    {
        var emailOnly = Alex;

        var rendered = Parse("{{contacts.phone}}").Render(emailOnly);

        Assert.Null(rendered.Value);
        Assert.Equal("contacts.phone", rendered.Missing);
    }

    [Fact]
    public void A_name_of_one_word_is_a_surname_rather_than_a_given_name()
    {
        var oneWord = Alex with { Names = ["Prince"] };

        Assert.Equal("Prince", Parse("{{names.last}}").Render(oneWord).Value);
        Assert.Null(Parse("{{names.first}}").Render(oneWord).Value);
    }
}
