// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.BrokerFixtures;

namespace Dbr.BrokerFixtures.Tests;

/// <summary>
/// The recorded company, answering over a real socket.
/// </summary>
/// <remarks>
/// Everything here goes through a real <see cref="HttpClient"/> to a real listener, which
/// is the point of the server existing at all: &sect;21.4 asks for the engine to run against
/// localhost exactly as it would against a broker's own domain, so the parts of it that
/// build a request and read a status line have to be in the path. A fake handler would
/// leave those untested and would make this file a test of a dictionary lookup.
/// </remarks>
public class BrokerFixtureServerTests
{
    private static FixtureScenario Scenario(string name)
    {
        var set = BrokerFixtureReader.Read().Find("example-broker");

        Assert.NotNull(set);

        var scenario = set.Find(name);

        Assert.NotNull(scenario);

        return scenario;
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(
        BrokerFixtureServer server,
        string path)
    {
        using var client = new HttpClient();

        var response = await client.GetAsync(new Uri(server.BaseAddress, path), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (response, body);
    }

    [Fact]
    public async Task It_serves_the_page_the_scenario_recorded()
    {
        await using var server = await BrokerFixtureServer.StartAsync(Scenario("one-match"));

        var (response, body) = await GetAsync(server, "/search?name=Alex+Whitfield");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Alex Whitfield", body, StringComparison.Ordinal);
        Assert.Contains("12 Rowan Lane", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A throttle is a status and a header, not a page.
    /// </summary>
    /// <remarks>
    /// The scenario that a fixture library of HTML files alone could not express, and the
    /// reason the manifest carries a status and headers at all.
    /// </remarks>
    [Fact]
    public async Task It_serves_a_throttle_with_the_header_that_is_the_instruction()
    {
        await using var server = await BrokerFixtureServer.StartAsync(Scenario("rate-limited"));

        var (response, _) = await GetAsync(server, "/search");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("120", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
    }

    [Theory]
    [InlineData("bot-wall", HttpStatusCode.Forbidden)]
    [InlineData("gateway-error", HttpStatusCode.ServiceUnavailable)]
    public async Task It_serves_the_status_a_refusal_actually_carries(
        string scenario,
        HttpStatusCode expected)
    {
        await using var server = await BrokerFixtureServer.StartAsync(Scenario(scenario));

        var (response, _) = await GetAsync(server, "/search");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task A_scenario_with_two_pages_serves_each_on_its_own_path()
    {
        await using var server = await BrokerFixtureServer.StartAsync(Scenario("listing-behind-a-link"));

        var (_, results) = await GetAsync(server, "/search?name=Alex+Whitfield");
        var (_, listing) = await GetAsync(server, "/profile/ep-8814720");

        Assert.DoesNotContain("Rowan Lane", results, StringComparison.Ordinal);
        Assert.Contains("Rowan Lane", listing, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path the scenario never recorded says so, rather than answering blankly.
    /// </summary>
    /// <remarks>
    /// An empty 404 reads as a company that removed a page; this one reads as a scenario
    /// that never had it. Those send whoever is debugging in opposite directions.
    /// </remarks>
    [Fact]
    public async Task A_request_the_scenario_never_recorded_says_which_scenario_it_was()
    {
        await using var server = await BrokerFixtureServer.StartAsync(Scenario("listing-behind-a-link"));

        // Every response in this scenario carries a path, so nothing answers a request that
        // matches neither.
        using var client = new HttpClient();

        var response = await client.GetAsync(
            new Uri(server.BaseAddress, "/somewhere-else"),
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("listing-behind-a-link", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// What was asked, not just what came back.
    /// </summary>
    /// <remarks>
    /// Half of what a search gets wrong is in the request. A recipe that built its query
    /// string from the wrong field gets a perfectly good page back, and a test that only
    /// read the answer would pass.
    /// </remarks>
    [Fact]
    public async Task It_records_what_it_was_asked()
    {
        await using var server = await BrokerFixtureServer.StartAsync(Scenario("one-match"));

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "dbr-test/1.0");

        await client.GetAsync(
            new Uri(server.BaseAddress, "/search?name=Alex+Whitfield&city=Sacramento"),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);

        Assert.Equal("GET", request.Method);
        Assert.Equal("/search", request.Path);
        Assert.Contains("Sacramento", request.QueryString, StringComparison.Ordinal);
        Assert.Equal("dbr-test/1.0", request.Header("User-Agent"));
    }

    /// <summary>Two recorded companies at once, which is the ordinary case for a scan.</summary>
    [Fact]
    public async Task Two_companies_can_answer_at_the_same_time()
    {
        await using var first = await BrokerFixtureServer.StartAsync(Scenario("one-match"));
        await using var second = await BrokerFixtureServer.StartAsync(Scenario("no-results"));

        Assert.NotEqual(first.BaseAddress, second.BaseAddress);

        var (_, found) = await GetAsync(first, "/search");
        var (_, nothing) = await GetAsync(second, "/search");

        Assert.Contains("12 Rowan Lane", found, StringComparison.Ordinal);
        Assert.DoesNotContain("12 Rowan Lane", nothing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_stops_answering_once_it_is_disposed()
    {
        var server = await BrokerFixtureServer.StartAsync(Scenario("one-match"));
        var address = server.BaseAddress;

        await server.DisposeAsync();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri(address, "/search"), TestContext.Current.CancellationToken));
    }
}
