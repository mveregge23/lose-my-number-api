// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.BrokerFixtures;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Search.Tests;

/// <summary>
/// What the engine reports, beyond which conclusion it reached.
/// </summary>
/// <remarks>
/// The dry-run beside this asserts the conclusion for every recorded page. This asks the
/// questions underneath it: which listings were reported, what each one was said to have
/// agreed with, and where each says it was found. Those are what a finding is actually made
/// of, and a search that reached "found" by reporting the wrong three listings would pass a
/// test that only read the outcome.
/// </remarks>
public class GenericWebSearchConnectorTests
{
    private static SearchRecipe Recipe() =>
        SearchRecipeReader.Read().Recipes.Single(recipe => recipe.Name == "example-broker");

    private static FixtureScenario Scenario(string name) =>
        BrokerFixtureReader.Read().Find("example-broker")!.Find(name)!;

    private static async Task<SearchResult> SearchAsync(string scenario)
    {
        var recipe = Recipe();

        await using var company = await BrokerFixtureServer.StartAsync(Scenario(scenario));

        return await Engine.For(recipe, company)
            .SearchAsync(Engine.Context(recipe), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_listing_that_agrees_on_a_name_and_an_address_says_both()
    {
        var found = Assert.IsType<SearchResult.Found>(await SearchAsync("one-match"));

        var candidate = Assert.Single(found.Candidates);

        Assert.Equal(
            [
                new FieldMatch(IdentityField.Names, MatchStrength.Exact),
                new FieldMatch(IdentityField.Addresses, MatchStrength.Exact),
            ],
            candidate.Matches);
    }

    /// <summary>
    /// Where the listing was found, resolved against the page it was read from.
    /// </summary>
    /// <remarks>
    /// A results page links to its listings relatively, and a reference that names a listing
    /// only to whatever page it was read from is useless by the time anybody follows it — the
    /// contract refuses one for exactly that reason.
    /// </remarks>
    [Fact]
    public async Task A_finding_points_somewhere_anybody_could_follow()
    {
        var found = Assert.IsType<SearchResult.Found>(await SearchAsync("one-match"));

        var source = Assert.Single(found.Candidates).SourceRef;

        Assert.True(source.IsAbsoluteUri);
        Assert.Equal("/profile/ep-8814720", source.AbsolutePath);
    }

    /// <summary>
    /// Three listings, three different degrees of agreement, and no scoring here.
    /// </summary>
    /// <remarks>
    /// The engine reports all three. Which of them anybody is ever shown is decided above this
    /// line, and the point of asserting the raw agreements is that the decision has something
    /// consistent to work from: the same listing produces the same matches whichever company
    /// it came from.
    /// </remarks>
    [Fact]
    public async Task Every_listing_on_the_page_is_reported_with_what_it_agreed_on()
    {
        var found = Assert.IsType<SearchResult.Found>(await SearchAsync("several-matches"));

        Assert.Equal(3, found.Candidates.Count);

        Assert.Equal(
            [
                new FieldMatch(IdentityField.Names, MatchStrength.Exact),
                new FieldMatch(IdentityField.Addresses, MatchStrength.Exact),
            ],
            found.Candidates[0].Matches);

        // Same surname, same given name, a middle initial the profile does not carry; the
        // right city at the wrong street.
        Assert.Equal(
            [
                new FieldMatch(IdentityField.Names, MatchStrength.Partial),
                new FieldMatch(IdentityField.Addresses, MatchStrength.Partial),
            ],
            found.Candidates[1].Matches);

        // A longer given name beginning the same way, and an address that agrees with nothing.
        Assert.Equal(
            [
                new FieldMatch(IdentityField.Names, MatchStrength.Partial),
                new FieldMatch(IdentityField.Addresses, MatchStrength.Conflicting),
            ],
            found.Candidates[2].Matches);
    }

    /// <summary>
    /// The whole pipeline's judgement about that page, in one place.
    /// </summary>
    /// <remarks>
    /// Not the engine's job and asserted here anyway, because this is the only test that sees
    /// both halves at once: what the search reported and what the floor makes of it. Three
    /// listings for one name, one of them shown.
    /// </remarks>
    [Fact]
    public async Task Only_the_listing_that_is_actually_this_person_clears_the_floor()
    {
        var found = Assert.IsType<SearchResult.Found>(await SearchAsync("several-matches"));

        var scores = found.Candidates
            .Select(candidate => MatchConfidence.Score(candidate.Matches))
            .ToArray();

        Assert.Equal(3, scores.Length);
        Assert.Equal(0.5, scores[0], 1e-9);
        Assert.Equal(1.5 / 4.5, scores[1], 1e-9);
        Assert.Equal(0.0, scores[2], 1e-9);

        Assert.Single(scores, MatchConfidence.ClearsFloor);
    }

    [Fact]
    public async Task A_page_that_lists_nobody_is_an_answer()
    {
        Assert.IsType<SearchResult.NothingFound>(await SearchAsync("no-results"));
    }

    /// <summary>
    /// The distinction that is expensive in both directions.
    /// </summary>
    /// <remarks>
    /// A results page whose class names have all changed holds no listings, exactly like a
    /// page that lists nobody. Telling somebody they are not listed anywhere on the strength
    /// of a redesign is the failure this separation exists to prevent, and the marker in the
    /// recipe is the only thing that separates them.
    /// </remarks>
    [Fact]
    public async Task A_page_that_has_been_redesigned_is_not_reported_as_holding_nobody()
    {
        var failed = Assert.IsType<SearchResult.Failed>(await SearchAsync("shape-changed"));

        Assert.Equal(SearchFailureReason.PageShapeChanged, failed.Reason);
        Assert.False(failed.Retryable);
    }

    [Fact]
    public async Task A_throttle_carries_how_long_it_asked_for()
    {
        var failed = Assert.IsType<SearchResult.Failed>(await SearchAsync("rate-limited"));

        Assert.Equal(SearchFailureReason.RateLimited, failed.Reason);
        Assert.True(failed.Retryable);
        Assert.Contains("120", failed.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A challenge served with 200, which no status code could catch.
    /// </summary>
    [Fact]
    public async Task A_challenge_page_is_a_refusal_rather_than_a_redesign()
    {
        var failed = Assert.IsType<SearchResult.Failed>(await SearchAsync("soft-bot-wall"));

        Assert.Equal(SearchFailureReason.Blocked, failed.Reason);
        Assert.False(failed.Retryable);
    }

    [Theory]
    [InlineData("bot-wall", SearchFailureReason.Blocked, false)]
    [InlineData("gateway-error", SearchFailureReason.Transient, true)]
    public async Task A_refusal_carried_by_a_status_is_read_from_it(
        string scenario,
        SearchFailureReason reason,
        bool retryable)
    {
        var failed = Assert.IsType<SearchResult.Failed>(await SearchAsync(scenario));

        Assert.Equal(reason, failed.Reason);
        Assert.Equal(retryable, failed.Retryable);
    }

    /// <summary>The request is built from the recipe, and escaped.</summary>
    [Fact]
    public async Task The_company_is_asked_what_the_recipe_says_to_ask()
    {
        var recipe = Recipe();

        await using var company = await BrokerFixtureServer.StartAsync(Scenario("one-match"));

        await Engine.For(recipe, company)
            .SearchAsync(Engine.Context(recipe), TestContext.Current.CancellationToken);

        var request = Assert.Single(company.Requests);

        Assert.Equal("/search", request.Path);
        Assert.Contains("name=Alex%20Whitfield", request.QueryString, StringComparison.Ordinal);
        Assert.Contains("city=Sacramento", request.QueryString, StringComparison.Ordinal);
    }

    /// <summary>
    /// An identity the query cannot be built from is a fault in the wiring, not in the company.
    /// </summary>
    [Fact]
    public async Task A_profile_missing_what_the_query_needs_never_reaches_the_company()
    {
        var recipe = Recipe();

        await using var company = await BrokerFixtureServer.StartAsync(Scenario("one-match"));

        var context = Engine.Context(recipe) with
        {
            ReleasedIdentity = new ProfileIdentityFields(Engine.Alex.Names, [], [], null),
        };

        var result = await Engine.For(recipe, company)
            .SearchAsync(context, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<SearchResult.Failed>(result);

        Assert.Equal(SearchFailureReason.Unsupported, failed.Reason);
        Assert.False(failed.Retryable);

        // And the company was never asked, which is the half that matters: a search that
        // cannot be built should not become traffic somebody has to explain.
        Assert.Empty(company.Requests);
    }

    /// <summary>
    /// Nothing the engine reports is outside what it was given.
    /// </summary>
    /// <remarks>
    /// The contract refuses a finding claiming a group the search never held, so a recipe
    /// comparing against something its release did not cover would fail its leg rather than
    /// its tests. Asserted here against every recorded page.
    /// </remarks>
    [Fact]
    public async Task The_engine_never_claims_a_group_it_was_not_given()
    {
        var recipe = Recipe();

        foreach (var scenario in BrokerFixtureReader.Read().Find("example-broker")!.Scenarios)
        {
            await using var company = await BrokerFixtureServer.StartAsync(scenario);

            var engine = Engine.For(recipe, company);

            var result = await engine.SearchAsync(
                Engine.Context(recipe),
                TestContext.Current.CancellationToken);

            if (result is SearchResult.Found found)
            {
                foreach (var match in found.Candidates.SelectMany(candidate => candidate.Matches))
                {
                    Assert.Contains(match.Field, engine.Capabilities.RequiredFields);
                }
            }
        }
    }
}
