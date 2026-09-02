// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Search;

namespace Dbr.BrokerFixtures;

/// <summary>
/// The one spelling of each thing a manifest can say.
/// </summary>
/// <remarks>
/// <para>
/// The same arrangement the catalog and monitoring vocabularies describe, pointed at a file
/// somebody writes by hand rather than at a column. What it buys is the same: one spelling,
/// checked in one place, so a fixture claiming an outcome and the code that would produce
/// that outcome cannot come to disagree about what it is called.
/// </para>
/// <para>
/// <b>The mapping is exhaustive over <see cref="SearchFailureReason"/> and throws on a
/// value it does not know.</b> That is what makes adding a way for a search to fail a
/// decision about the fixture library too: somebody adding one has to say how a recorded
/// page would demonstrate it, or record that no page can.
/// </para>
/// </remarks>
public static class FixtureVocabulary
{
    /// <summary>How a failure is written in a manifest.</summary>
    public static string ToWire(SearchFailureReason reason) => reason switch
    {
        SearchFailureReason.Transient => "transient",
        SearchFailureReason.RateLimited => "rate-limited",
        SearchFailureReason.PageShapeChanged => "page-shape-changed",
        SearchFailureReason.Blocked => "blocked",

        // Decided before anything is fetched — a missing field, a site variant the search
        // does not handle — so no recorded page can cause it. Refused rather than spelled,
        // which is what stops a fixture claiming to demonstrate something that happens
        // strictly earlier than the fixture.
        SearchFailureReason.Unsupported => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "A search reports Unsupported before it fetches anything, so no recorded page "
            + "can produce it. There is deliberately no spelling for it in a manifest."),

        _ => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "Unspelled search failure. Adding a way for a search to fail means deciding how "
            + "a recorded page would demonstrate it — or recording that none can, as "
            + "Unsupported does."),
    };

    /// <summary>What a manifest's <c>expect</c> means, or nothing when it means nothing.</summary>
    public static SearchExpectation? ParseExpectation(string? value) => value switch
    {
        "found" => SearchExpectation.Found,
        "nothing-found" => SearchExpectation.NothingFound,
        "transient" => SearchExpectation.Failed(SearchFailureReason.Transient),
        "rate-limited" => SearchExpectation.Failed(SearchFailureReason.RateLimited),
        "page-shape-changed" => SearchExpectation.Failed(SearchFailureReason.PageShapeChanged),
        "blocked" => SearchExpectation.Failed(SearchFailureReason.Blocked),
        _ => null,
    };

    /// <summary>What a manifest's <c>kind</c> means, or nothing when it means nothing.</summary>
    public static FixtureKind? ParseKind(string? value) => value switch
    {
        "search" => FixtureKind.Search,
        _ => null,
    };

    /// <summary>
    /// Every outcome a recorded page is expected to be able to demonstrate.
    /// </summary>
    /// <remarks>
    /// Derived from the enum rather than listed, so that the coverage check reads the same
    /// list the contract does. A failure reason with no spelling — today only
    /// <see cref="SearchFailureReason.Unsupported"/> — is left out here for the reason it
    /// has no spelling, rather than by being forgotten.
    /// </remarks>
    public static IReadOnlyList<SearchExpectation> EveryExpectation { get; } =
    [
        SearchExpectation.Found,
        SearchExpectation.NothingFound,
        .. Enum.GetValues<SearchFailureReason>()
            .Where(reason => reason is not SearchFailureReason.Unsupported)
            .Select(SearchExpectation.Failed),
    ];
}
