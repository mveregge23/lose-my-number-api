// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Search.Tests;

/// <summary>
/// What a recipe may say, and what it is refused for saying.
/// </summary>
/// <remarks>
/// Every refusal here is something that reads fine in a diff. That is the point of the file:
/// a recipe gets a lighter review bar than code, so the things a reviewer cannot see have to
/// be the things the reader will not accept.
/// </remarks>
public class SearchRecipeReaderTests
{
    private const string BrokerId = "2f6b1c48-9d3a-4e57-b8a1-0c5e7f9d24b3";

    private static string Yaml(
        string brokerId = BrokerId,
        string query = "/search?name={{names.full}}",
        string item = ".result",
        string link = ".result-name",
        string noResults = ".no-results",
        string? blocked = null,
        string fields = "  names: .result-name") =>
        $"""
         brokerId: {brokerId}
         description: A recipe written by a test.
         query: "{query}"
         item: "{item}"
         link: "{link}"
         noResults: "{noResults}"
         {(blocked is null ? string.Empty : $"blocked: \"{blocked}\"")}
         fields:
         {fields}
         """;

    private static SearchRecipe? Read(string yaml, out List<string> problems)
    {
        problems = [];

        return SearchRecipeReader.ReadOne("test-broker", yaml, problems);
    }

    [Fact]
    public void A_recipe_that_says_everything_it_has_to_is_read()
    {
        var recipe = Read(Yaml(), out var problems);

        Assert.Empty(problems);
        Assert.NotNull(recipe);
        Assert.Equal(Guid.Parse(BrokerId), recipe.BrokerId);
    }

    /// <summary>
    /// The refusal that is about what a contributed document must not be able to do.
    /// </summary>
    /// <remarks>
    /// Everything else here is a recipe being wrong. This one is a recipe being dangerous: a
    /// document that names where a request goes is a document that can send somebody's name
    /// to any address on the internet, and it would arrive for review as "this YAML now points
    /// at a different URL".
    /// </remarks>
    [Theory]
    [InlineData("https://somewhere-else.test/collect?name={{names.full}}")]
    [InlineData("http://127.0.0.1:9000/?n={{names.full}}")]

    // Protocol-relative, and the case that is easy to miss: there is no scheme in it at all,
    // so a check looking for "://" waves it through — and resolving it against an origin keeps
    // the scheme and replaces the host. It reads like a path and is not one. Found by deleting
    // the check to see which tests noticed, and discovering that this form did not.
    [InlineData("//somewhere-else.test/collect?name={{names.full}}")]
    public void A_recipe_cannot_name_where_the_request_goes(string query)
    {
        Read(Yaml(query: query), out var problems);

        Assert.Contains(problems, problem => problem.Contains("whole address", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the resolved address really is on the company's own site.
    /// </summary>
    /// <remarks>
    /// The refusals above are about the document. This is about the consequence, asserted
    /// against the same resolution the engine performs — because what actually matters is not
    /// which strings are rejected but that no accepted one can reach another host.
    /// </remarks>
    [Theory]
    [InlineData("/search?name={{names.full}}")]
    [InlineData("/a/b/../../search?name={{names.full}}")]
    public void An_accepted_query_can_only_reach_the_company_it_was_read_for(string query)
    {
        var recipe = Read(Yaml(query: query), out var problems);

        Assert.Empty(problems);
        Assert.NotNull(recipe);

        var rendered = recipe.Query.Render(
            new ProfileIdentityFields(["Alex Whitfield"], [], [], null));

        Assert.NotNull(rendered.Value);

        var resolved = new Uri(new Uri("https://example-broker.test"), rendered.Value);

        Assert.Equal("example-broker.test", resolved.Host);
        Assert.Equal(Uri.UriSchemeHttps, resolved.Scheme);
    }

    [Fact]
    public void A_query_that_does_not_start_at_the_root_is_refused()
    {
        Read(Yaml(query: "search?name={{names.full}}"), out var problems);

        Assert.Contains(problems, problem => problem.Contains("begin with '/'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recipe_that_searches_for_nobody_in_particular_is_refused()
    {
        Read(Yaml(query: "/search?everyone=true"), out var problems);

        Assert.Contains(
            problems,
            problem => problem.Contains("whoever it was searching for", StringComparison.Ordinal));
    }

    [Fact]
    public void A_placeholder_outside_the_vocabulary_is_refused()
    {
        Read(Yaml(query: "/search?q={{names.middle}}"), out var problems);

        Assert.Contains(problems, problem => problem.Contains("names.middle", StringComparison.Ordinal));
    }

    /// <summary>
    /// A selector is checked by handing it to the parser that will run it.
    /// </summary>
    /// <remarks>
    /// Which means the check is exactly as strict as AngleSharp is and no stricter — a typo
    /// that happens to be a legal selector matching nothing is not caught here, and cannot be:
    /// whether <c>.reslt</c> matches anything is a question about a page rather than about a
    /// document. The dry-run against recorded pages is what catches that one.
    /// </remarks>
    [Theory]
    [InlineData("item")]
    [InlineData("link")]
    [InlineData("noResults")]
    public void A_selector_that_is_not_one_is_refused(string which)
    {
        var yaml = which switch
        {
            "item" => Yaml(item: ">>>"),
            "link" => Yaml(link: "a[href"),
            _ => Yaml(noResults: ".no-results["),
        };

        Read(yaml, out var problems);

        Assert.Contains(problems, problem => problem.Contains("is not one", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recipe_comparing_a_listing_against_nothing_is_refused()
    {
        Read(Yaml(fields: "  {}"), out var problems);

        Assert.Contains(
            problems,
            problem => problem.Contains("against nothing", StringComparison.Ordinal));
    }

    /// <summary>
    /// Refused rather than guessed at, which is the honest version of "not yet".
    /// </summary>
    /// <remarks>
    /// Pages show an age far more often than a date, and what an age agrees with needs a
    /// "today" a recipe cannot see and a tolerance nobody has decided on. Accepting the
    /// selector and comparing badly would produce findings whose confidence nobody could
    /// explain.
    /// </remarks>
    [Fact]
    public void Comparing_a_listing_against_a_date_of_birth_is_refused()
    {
        Read(Yaml(fields: "  dateOfBirth: .result-age"), out var problems);

        Assert.Contains(problems, problem => problem.Contains("age rather than a date", StringComparison.Ordinal));
    }

    [Fact]
    public void A_group_that_is_not_a_group_of_an_identity_is_refused()
    {
        Read(Yaml(fields: "  relatives: .result-relatives"), out var problems);

        Assert.Contains(problems, problem => problem.Contains("relatives", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void A_recipe_that_names_no_company_is_refused(string brokerId)
    {
        Read(Yaml(brokerId: brokerId), out var problems);

        Assert.Contains(problems, problem => problem.Contains("not an id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recipe_that_does_not_say_what_it_does_is_refused()
    {
        // A plain raw string rather than an interpolated one: the placeholder braces a recipe
        // is written with are the interpolation delimiter, and escaping them here would make
        // the document under test unreadable.
        Read(
            """
            brokerId: 2f6b1c48-9d3a-4e57-b8a1-0c5e7f9d24b3
            query: "/search?name={{names.full}}"
            item: ".result"
            link: ".result-name"
            noResults: ".no-results"
            fields:
              names: .result-name
            """,
            out var problems);

        Assert.Contains(problems, problem => problem.Contains("what it searches", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_problem_is_reported_rather_than_the_first()
    {
        Read(Yaml(query: "/search?q={{names.middle}}", item: ">>>"), out var problems);

        Assert.Contains(problems, problem => problem.Contains("names.middle", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("is not one", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a recipe needs is the union of what it writes and what it compares.
    /// </summary>
    /// <remarks>
    /// The half that is easy to leave out. A recipe searching by name and then reporting
    /// whether the listing's address agreed has to have been given the address to compare it
    /// with — and if the derivation only read the query, the release would arrive without it
    /// and the finding would be refused by the contract for claiming a group the search never
    /// held.
    /// </remarks>
    [Fact]
    public void A_recipe_needs_what_it_compares_as_well_as_what_it_writes()
    {
        var recipe = Read(
            Yaml(
                query: "/search?name={{names.full}}",
                fields: "  names: .result-name\n  addresses: .result-address"),
            out var problems);

        Assert.Empty(problems);
        Assert.NotNull(recipe);

        Assert.Contains(IdentityField.Names, recipe.RequiredFields);
        Assert.Contains(IdentityField.Addresses, recipe.RequiredFields);
        Assert.DoesNotContain(IdentityField.Contacts, recipe.RequiredFields);
    }

    [Fact]
    public void The_shipped_recipes_are_readable()
    {
        var read = SearchRecipeReader.Read();

        Assert.Empty(read.Problems);
        Assert.NotEmpty(read.Recipes);
    }

    [Fact]
    public void A_directory_with_recorded_pages_and_no_recipe_is_not_a_problem()
    {
        // Most of the catalog will look like this for a long time, and it is how "nothing
        // knows how to search this company" is written down rather than an error.
        var root = Path.Combine(Path.GetTempPath(), $"dbr-recipes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "some-broker", "fixtures"));

        try
        {
            var read = SearchRecipeReader.Read(root);

            Assert.Empty(read.Problems);
            Assert.Empty(read.Recipes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
