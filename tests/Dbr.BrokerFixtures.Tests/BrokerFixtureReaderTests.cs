// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.BrokerFixtures;
using Dbr.Domain.Search;

namespace Dbr.BrokerFixtures.Tests;

/// <summary>
/// Reading the recorded pages, and refusing the ones that would test nothing.
/// </summary>
/// <remarks>
/// Half of these are about the library as it actually ships &mdash; the real manifest, the
/// real files &mdash; and half are about manifests written to be wrong. The second half
/// matters more than it looks: every refusal here is a mistake that reads perfectly well in
/// a diff, and whose symptom is a test that passes without exercising anything.
/// </remarks>
public class BrokerFixtureReaderTests
{
    private static FixtureReadResult Real() => BrokerFixtureReader.Read();

    // ---------------------------------------------------------------- the shipped set

    [Fact]
    public void The_recorded_pages_that_ship_are_usable()
    {
        var read = Real();

        Assert.Empty(read.Problems);
        Assert.NotEmpty(read.Sets);
    }

    [Fact]
    public void The_example_company_is_there_and_its_scenarios_have_bodies()
    {
        var set = Real().Find("example-broker");

        Assert.NotNull(set);
        Assert.NotEmpty(set.Scenarios);

        foreach (var scenario in set.Scenarios)
        {
            Assert.NotEmpty(scenario.Responses);
            Assert.NotEmpty(scenario.Description);

            foreach (var response in scenario.Responses)
            {
                Assert.NotEmpty(response.Body);
            }
        }
    }

    /// <summary>
    /// The claim &sect;21.4 actually makes: the whole result type is exercised somewhere.
    /// </summary>
    /// <remarks>
    /// Across the library rather than per company, which is the reading that matters. A
    /// per-company rule would mean inventing a bot wall for every broker that has never
    /// served one, and the point is that no case of the result type goes unrepresented &mdash;
    /// not that every company demonstrates every case.
    /// </remarks>
    [Fact]
    public void Every_conclusion_a_search_can_reach_is_recorded_somewhere()
    {
        var uncovered = BrokerFixtureReader.Uncovered(Real());

        Assert.Empty(uncovered);
    }

    /// <summary>
    /// And the check that says so is capable of saying otherwise.
    /// </summary>
    /// <remarks>
    /// The test above asserts an empty list, which a coverage check that always answered
    /// "nothing missing" would satisfy just as happily — so the claim it makes would be
    /// worth nothing. This is the half that gives it weight: a library recording only one
    /// outcome is reported as missing exactly the others.
    /// </remarks>
    [Fact]
    public void A_library_missing_an_outcome_is_told_so()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario(expect: "found"));
        temp.Body("acme", "page.html", "<html></html>");

        var uncovered = BrokerFixtureReader.Uncovered(BrokerFixtureReader.Read(temp.Root));

        Assert.DoesNotContain(SearchExpectation.Found, uncovered);
        Assert.Contains(SearchExpectation.NothingFound, uncovered);
        Assert.Contains(SearchExpectation.Failed(SearchFailureReason.Blocked), uncovered);

        Assert.Equal(FixtureVocabulary.EveryExpectation.Count - 1, uncovered.Count);
    }

    /// <summary>
    /// The one outcome no page can produce, and it is left out on purpose.
    /// </summary>
    [Fact]
    public void Nothing_claims_to_record_a_failure_decided_before_the_fetch()
    {
        Assert.DoesNotContain(
            SearchExpectation.Failed(SearchFailureReason.Unsupported),
            FixtureVocabulary.EveryExpectation);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FixtureVocabulary.ToWire(SearchFailureReason.Unsupported));
    }

    [Fact]
    public void A_throttle_is_recorded_with_the_header_that_is_the_instruction()
    {
        var scenario = Real().Find("example-broker")!.Find("rate-limited");

        Assert.NotNull(scenario);

        var response = Assert.Single(scenario.Responses);

        Assert.Equal(429, response.Status);
        Assert.Equal("120", response.Headers["Retry-After"]);
    }

    /// <summary>A scenario with two pages tells them apart by path.</summary>
    [Fact]
    public void A_scenario_can_answer_more_than_one_request()
    {
        var scenario = Real().Find("example-broker")!.Find("listing-behind-a-link");

        Assert.NotNull(scenario);
        Assert.Equal(2, scenario.Responses.Count);

        var results = scenario.ResponseFor("/search");
        var listing = scenario.ResponseFor("/profile/ep-8814720");

        Assert.NotNull(results);
        Assert.NotNull(listing);
        Assert.NotEqual(results.Body, listing.Body);

        // The address is behind the link and not on the results page, which is the whole
        // reason the scenario has two of them.
        Assert.DoesNotContain("Rowan Lane", results.Body, StringComparison.Ordinal);
        Assert.Contains("Rowan Lane", listing.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_with_no_path_answers_anything()
    {
        var scenario = Real().Find("example-broker")!.Find("one-match");

        Assert.NotNull(scenario);
        Assert.NotNull(scenario.ResponseFor("/search"));
        Assert.NotNull(scenario.ResponseFor("/anything/at/all"));
    }

    // ------------------------------------------------------- manifests written to be wrong

    [Fact]
    public void A_directory_with_no_manifest_is_a_problem_rather_than_an_empty_company()
    {
        using var temp = new TempFixtures();
        temp.Directory("orphan/fixtures");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Empty(read.Sets);
        Assert.Contains(read.Problems, problem => problem.Contains("orphan", StringComparison.Ordinal));
    }

    /// <summary>
    /// A manifest naming a company from inside another's directory.
    /// </summary>
    /// <remarks>
    /// The refusal worth having most. Left alone it serves one company's pages to a recipe
    /// that asked for another's, and the test built on it passes.
    /// </remarks>
    [Fact]
    public void A_manifest_naming_the_wrong_company_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", broker: "not-acme");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Empty(read.Sets);
        Assert.Contains(read.Problems, problem => problem.Contains("not-acme", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scenario_pointing_at_a_page_that_is_not_there_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario(body: "missing.html"));

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("missing.html", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scenario_pointing_at_an_empty_page_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario(body: "blank.html"));
        temp.Body("acme", "blank.html", "   ");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("blank.html", StringComparison.Ordinal));
    }

    [Fact]
    public void An_outcome_a_search_cannot_reach_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario(expect: "probably"));
        temp.Body("acme", "page.html", "<html></html>");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("probably", StringComparison.Ordinal));
    }

    /// <summary>
    /// The removal half is not expressible yet, and says so rather than being ignored.
    /// </summary>
    [Fact]
    public void A_kind_this_build_has_no_result_type_for_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario(kind: "removal"));
        temp.Body("acme", "page.html", "<html></html>");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("removal", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_scenarios_with_one_name_are_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario() + Scenario());
        temp.Body("acme", "page.html", "<html></html>");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("two scenarios", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scenario_serving_nothing_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest(
            "acme",
            scenarios:
            """
                - name: empty
                  kind: search
                  expect: found
                  description: Serves nothing at all.
                  responses: []
            """);

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("serves nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_status_that_is_not_a_status_is_refused()
    {
        using var temp = new TempFixtures();
        temp.Manifest("acme", scenarios: Scenario(status: 42));
        temp.Body("acme", "page.html", "<html></html>");

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("42", StringComparison.Ordinal));
    }

    /// <summary>
    /// Everything wrong, rather than the first thing wrong.
    /// </summary>
    /// <remarks>
    /// The same policy the legal-basis reader keeps, and for the same reason: a validator
    /// that stops at the first error turns one review into four.
    /// </remarks>
    [Fact]
    public void Every_problem_is_reported_rather_than_the_first()
    {
        using var temp = new TempFixtures();
        temp.Manifest(
            "acme",
            scenarios: Scenario(name: "one", body: "gone.html") + Scenario(name: "two", expect: "maybe"));

        var read = BrokerFixtureReader.Read(temp.Root);

        Assert.Contains(read.Problems, problem => problem.Contains("gone.html", StringComparison.Ordinal));
        Assert.Contains(read.Problems, problem => problem.Contains("maybe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_directory_says_so_rather_than_throwing()
    {
        var read = BrokerFixtureReader.Read(
            Path.Combine(Path.GetTempPath(), $"no-fixtures-{Guid.NewGuid():N}"));

        Assert.Empty(read.Sets);
        Assert.Single(read.Problems);
    }

    private static string Scenario(
        string name = "a-scenario",
        string kind = "search",
        string expect = "found",
        string body = "page.html",
        int status = 200) =>
        $"""
            - name: {name}
              kind: {kind}
              expect: {expect}
              description: A scenario written by a test.
              responses:
                - status: {status}
                  body: {body}

        """;

    /// <summary>A throwaway fixture tree, for the manifests that are meant to be wrong.</summary>
    private sealed class TempFixtures : IDisposable
    {
        public TempFixtures()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dbr-fixtures-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Directory(string relative) =>
            System.IO.Directory.CreateDirectory(Path.Combine(Root, relative));

        public void Manifest(string brokerId, string? broker = null, string? scenarios = null)
        {
            var fixtures = Path.Combine(Root, brokerId, "fixtures");
            System.IO.Directory.CreateDirectory(fixtures);

            var body = scenarios ?? Scenario();

            File.WriteAllText(
                Path.Combine(fixtures, "fixtures.yaml"),
                $"""
                 broker: {broker ?? brokerId}
                 description: A company invented by a test.
                 scenarios:
                 {body}
                 """);
        }

        public void Body(string brokerId, string name, string content) =>
            File.WriteAllText(Path.Combine(Root, brokerId, "fixtures", name), content);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over.
            }
        }
    }
}
