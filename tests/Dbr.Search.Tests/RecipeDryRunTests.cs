// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.BrokerFixtures;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Search.Tests;

/// <summary>
/// Every recipe, run against every page recorded for its company.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what "reviewed as data" actually rests on.</b> §9.1 gives recipes a lighter
/// review bar than code, and that is only defensible if a change to one is checked by
/// something other than a reviewer reading selectors. §12.1 asks for exactly this: a recipe
/// dry-run against recorded pages rather than the live site, on the pull request.
/// </para>
/// <para>
/// It is a test rather than a separate tool because CI already runs the suite, and a check
/// nobody has to remember to wire into a workflow is one that cannot be left out of one. What
/// it exercises is the real engine over a real socket against the real recorded pages — the
/// only invented thing in the path is the company.
/// </para>
/// <para>
/// <b>The fixture declares the answer and the recipe has to produce it.</b> That is the whole
/// arrangement: neither half can be quietly adjusted to agree with the other, because a
/// fixture saying "this page means the company holds nothing" is a claim about the page, and a
/// recipe reading it as anything else is wrong regardless of which was written first.
/// </para>
/// </remarks>
public class RecipeDryRunTests
{
    /// <summary>Every recipe paired with every scenario recorded for its company.</summary>
    public static TheoryData<string, string> EveryRecipeAndScenario()
    {
        var data = new TheoryData<string, string>();
        var fixtures = BrokerFixtureReader.Read();

        foreach (var recipe in SearchRecipeReader.Read().Recipes)
        {
            var set = fixtures.Find(recipe.Name);

            if (set is null)
            {
                continue;
            }

            foreach (var scenario in set.Scenarios)
            {
                data.Add(recipe.Name, scenario.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRecipeAndScenario))]
    public async Task A_recipe_reaches_the_conclusion_its_fixture_declares(
        string recipeName,
        string scenarioName)
    {
        var recipe = SearchRecipeReader.Read().Recipes
            .Single(candidate => candidate.Name == recipeName);

        var scenario = BrokerFixtureReader.Read().Find(recipeName)!.Find(scenarioName)!;

        await using var company = await BrokerFixtureServer.StartAsync(scenario);

        var result = await Engine.For(recipe, company)
            .SearchAsync(Engine.Context(recipe), TestContext.Current.CancellationToken);

        Assert.Equal(scenario.Expect, Engine.Reached(result));
    }

    /// <summary>
    /// And every recipe answers in a way the contract allows.
    /// </summary>
    /// <remarks>
    /// The dry-run above asks whether the conclusion is right. This asks whether the engine
    /// was entitled to it at all — a finding claiming a group the search was never given, two
    /// findings pointing at one listing, a result reporting findings and listing none. Those
    /// are refused at the worker's boundary, so a recipe producing one would fail its leg
    /// rather than its test, at a moment nobody is watching.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryRecipeAndScenario))]
    public async Task A_recipe_answers_within_the_contract(string recipeName, string scenarioName)
    {
        var recipe = SearchRecipeReader.Read().Recipes
            .Single(candidate => candidate.Name == recipeName);

        var scenario = BrokerFixtureReader.Read().Find(recipeName)!.Find(scenarioName)!;

        await using var company = await BrokerFixtureServer.StartAsync(scenario);

        var context = Engine.Context(recipe);
        var engine = Engine.For(recipe, company);

        Assert.Null(SearchContract.Refuse(engine.Capabilities, context));

        var result = await engine.SearchAsync(context, TestContext.Current.CancellationToken);

        Assert.Null(SearchContract.Refuse(engine.Capabilities, result));
    }

    /// <summary>
    /// Every recipe that ships is readable, and every one has recorded pages.
    /// </summary>
    /// <remarks>
    /// A recipe with no fixtures is a recipe nothing above checks, and it would pass the
    /// dry-run by contributing no cases to it — which is the way this kind of check usually
    /// stops working.
    /// </remarks>
    [Fact]
    public void Every_recipe_is_readable_and_has_pages_to_be_run_against()
    {
        var read = SearchRecipeReader.Read();

        Assert.Empty(read.Problems);
        Assert.NotEmpty(read.Recipes);

        var fixtures = BrokerFixtureReader.Read();

        foreach (var recipe in read.Recipes)
        {
            var set = fixtures.Find(recipe.Name);

            Assert.True(
                set is not null && set.Scenarios.Count > 0,
                $"'{recipe.Name}' has a recipe and no recorded pages, so nothing checks it.");
        }
    }
}

/// <summary>Wiring the real engine at a recorded company.</summary>
internal static class Engine
{
    /// <summary>The identity every recorded page here was written around.</summary>
    internal static ProfileIdentityFields Alex { get; } = new(
        ["Alex Whitfield"],
        [
            new ProfileAddress(
                Guid.NewGuid(),
                "12 Rowan Lane",
                null,
                "Sacramento",
                "CA",
                "95814",
                "US"),
        ],
        [new ProfileContact(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test")],
        new DateOnly(1985, 4, 17));

    /// <summary>
    /// The engine, pointed at the recorded company instead of the real one.
    /// </summary>
    /// <remarks>
    /// The origin is the only substitution, and it is a one-line function rather than a fake
    /// client — so the request is built, sent, and its status line read exactly as it would be
    /// against a company's own domain.
    /// </remarks>
    internal static GenericWebSearchConnector For(SearchRecipe recipe, BrokerFixtureServer company) =>
        new(recipe, new HttpClient(), _ => company.BaseAddress);

    internal static SearchContext Context(SearchRecipe recipe) =>
        new(
            Guid.NewGuid(),
            new SearchTarget(recipe.BrokerId, "example-broker.test"),

            // Only what the recipe declared it needs, which is what a real release would hand
            // over — passing the whole identity would let a recipe match on a group it never
            // asked for and the contract would refuse the result.
            Released(recipe),
            AttemptNumber: 1);

    private static ProfileIdentityFields Released(SearchRecipe recipe) =>
        new(
            recipe.RequiredFields.Contains(IdentityField.Names) ? Alex.Names : [],
            recipe.RequiredFields.Contains(IdentityField.Addresses) ? Alex.Addresses : [],
            recipe.RequiredFields.Contains(IdentityField.Contacts) ? Alex.Contacts : [],
            recipe.RequiredFields.Contains(IdentityField.DateOfBirth) ? Alex.DateOfBirth : null);

    /// <summary>What a result says, in the vocabulary a fixture declares.</summary>
    internal static SearchExpectation Reached(SearchResult result) => result switch
    {
        SearchResult.Found => SearchExpectation.Found,
        SearchResult.NothingFound => SearchExpectation.NothingFound,
        SearchResult.Failed failed => SearchExpectation.Failed(failed.Reason),
        _ => throw new ArgumentOutOfRangeException(
            nameof(result),
            result,
            "A search outcome with no reading. The result type is closed, so this means a case "
            + "was added without deciding how a fixture would declare it."),
    };
}
