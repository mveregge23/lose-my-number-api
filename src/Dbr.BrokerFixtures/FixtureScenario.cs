// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Search;

namespace Dbr.BrokerFixtures;

/// <summary>Which half of the pipeline a recorded page is about.</summary>
/// <remarks>
/// One member today, and that is deliberate rather than unfinished. §21.4 describes
/// fixtures for the removal side too — a confirmation page, an "already opted out" page, a
/// CAPTCHA where a confirmation was expected — and the result type those would be declared
/// against does not exist yet. Adding the member before the enum it validates against
/// would mean a fixture claiming an outcome nothing can check it produced, which is the
/// shape of claim this whole file exists to prevent.
/// </remarks>
public enum FixtureKind
{
    /// <summary>A page a search reads, asking what a company holds.</summary>
    Search,
}

/// <summary>What a search should conclude from a recorded page.</summary>
/// <remarks>
/// <para>
/// <b>The fixture declares the answer, and that is what makes the library worth having.</b>
/// A recorded page on its own only proves an engine does not crash on it. Recording what
/// the page <i>means</i> turns the set into something a test can be exhaustive against:
/// every case of <see cref="SearchResult"/> is exercised somewhere rather than only the
/// happy path, which is the whole of what §21.4 asks for.
/// </para>
/// <para>
/// <b><see cref="SearchFailureReason.Unsupported"/> is deliberately not expressible.</b> It
/// says the search cannot do what this attempt asks of it — a missing field, a site variant
/// it does not handle — and every one of those is decided before anything is fetched. No
/// recorded page can cause it, so a fixture claiming it would be describing something that
/// happens strictly earlier than the fixture.
/// </para>
/// </remarks>
/// <param name="Reason">
/// Why it failed, when it failed. Null for the two outcomes that are answers rather than
/// failures.
/// </param>
public sealed record SearchExpectation(SearchExpectationKind Kind, SearchFailureReason? Reason)
{
    /// <summary>The company answered and holds listings that might be this person.</summary>
    public static SearchExpectation Found { get; } = new(SearchExpectationKind.Found, null);

    /// <summary>The company answered and holds nothing.</summary>
    public static SearchExpectation NothingFound { get; } =
        new(SearchExpectationKind.NothingFound, null);

    /// <summary>The company did not answer the question, for this reason.</summary>
    public static SearchExpectation Failed(SearchFailureReason reason) =>
        new(SearchExpectationKind.Failed, reason);

    /// <summary>How this expectation is written in a manifest.</summary>
    public string Spelling => Kind switch
    {
        SearchExpectationKind.Found => "found",
        SearchExpectationKind.NothingFound => "nothing-found",
        SearchExpectationKind.Failed => FixtureVocabulary.ToWire(Reason!.Value),
        _ => throw new InvalidOperationException($"Unspelled expectation kind {Kind}."),
    };

    public override string ToString() => Spelling;
}

/// <summary>Whether a search answered, and if not, that it did not.</summary>
public enum SearchExpectationKind
{
    Found,
    NothingFound,
    Failed,
}

/// <summary>One response the recorded company gives.</summary>
/// <remarks>
/// <para>
/// A scenario is usually one of these and occasionally several: a recipe that reads a
/// results page and then follows a link into a listing makes two requests, and the second
/// one is a different page. <see cref="Path"/> is how they are told apart, and it is
/// optional because most scenarios have nothing to tell apart — a response with no path
/// answers whatever is asked, which is the honest description of "this company is
/// rate-limiting today".
/// </para>
/// <para>
/// <b>The status and the headers are part of the recording.</b> A 429 with a
/// <c>Retry-After</c> is not an HTML page and cannot be recorded as one, and the failures
/// worth being sure about — throttling, a bot wall, a bad gateway — are all carried above
/// the body rather than in it.
/// </para>
/// </remarks>
/// <param name="Path">
/// The request path this answers, or <see langword="null"/> to answer anything. Matched as
/// a prefix, because a recipe's URL carries a query string this file has no business
/// spelling out.
/// </param>
/// <param name="Body">
/// The recorded page, already read. Held as text rather than as a filename because a
/// scenario that has been read is one whose files are known to be there — a consumer
/// holding a path would have to re-answer that question at the moment it serves a request.
/// </param>
public sealed record FixtureResponse(
    string? Path,
    int Status,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string ContentType)
{
    /// <summary>Whether this is the response to give for a request on this path.</summary>
    public bool Answers(string requestPath) =>
        Path is null || requestPath.StartsWith(Path, StringComparison.Ordinal);
}

/// <summary>
/// One thing a recorded company can do, and what a search should make of it.
/// </summary>
/// <param name="Description">
/// Why this scenario exists, for whoever reads the manifest. Not optional: a recorded page
/// is a wall of somebody else's markup, and "what is this one for" is exactly the question
/// a diff cannot answer.
/// </param>
public sealed record FixtureScenario(
    string Name,
    FixtureKind Kind,
    SearchExpectation Expect,
    string Description,
    IReadOnlyList<FixtureResponse> Responses)
{
    /// <summary>The response to a request on this path, or nothing when none matches.</summary>
    public FixtureResponse? ResponseFor(string requestPath) =>
        Responses.FirstOrDefault(response => response.Answers(requestPath));
}

/// <summary>Every scenario recorded for one company.</summary>
/// <param name="BrokerId">
/// The catalog's identity for the company, which is also the directory these live in. The
/// two are checked against each other when the manifest is read, because a manifest naming
/// one company from inside another's directory would serve the wrong pages to a recipe
/// that asked for the right ones.
/// </param>
public sealed record BrokerFixtureSet(
    string BrokerId,
    string Description,
    IReadOnlyList<FixtureScenario> Scenarios)
{
    /// <summary>One scenario by name, or nothing.</summary>
    public FixtureScenario? Find(string name) =>
        Scenarios.FirstOrDefault(scenario =>
            string.Equals(scenario.Name, name, StringComparison.Ordinal));
}
