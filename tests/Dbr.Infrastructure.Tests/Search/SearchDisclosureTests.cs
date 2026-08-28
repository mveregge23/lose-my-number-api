// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Infrastructure.Tests.Search;

/// <summary>
/// What the search types turn into when something asks them for a string.
/// </summary>
/// <remarks>
/// A search is the one place in this system that handles two people's data at once: the
/// identity it was released, and the broker's copy of it that it went looking for. Both
/// have to stay out of a log line, and a record prints every member it has, so each of
/// these is a generated <c>ToString</c> away from not being covered.
/// </remarks>
public class SearchDisclosureTests
{
    private static readonly ProfileAddress Address =
        new(Guid.NewGuid(), "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US");

    private static readonly SearchContext Context = new(
        Guid.NewGuid(),
        new SearchTarget(Guid.NewGuid(), "example-broker.test"),
        new ProfileIdentityFields(["Alex Whitfield"], [Address], [], new DateOnly(1985, 4, 17)),
        1);

    [Fact]
    public void Interpolating_a_context_yields_none_of_the_identity_it_carries()
    {
        var interpolated = $"{Context}";

        Assert.DoesNotContain("Alex Whitfield", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Rowan Lane", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Sacramento", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("1985", interpolated, StringComparison.Ordinal);
        Assert.Contains("withheld", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The context is otherwise the useful thing to print, and stays that way.
    /// </summary>
    /// <remarks>
    /// A type that withholds everything gets worked around rather than used. What is left
    /// here — the run, the company, the attempt — is what somebody following a failure
    /// through a log actually needs, and none of it is about a person.
    /// </remarks>
    [Fact]
    public void What_is_left_of_a_context_is_still_worth_logging()
    {
        var interpolated = $"{Context}";

        Assert.Contains(Context.ScanId.ToString(), interpolated, StringComparison.Ordinal);
        Assert.Contains("example-broker.test", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A listing's address is the broker's copy of somebody's identity, not a pointer to it.
    /// </summary>
    /// <remarks>
    /// Broker profile URLs routinely spell out the name, the city and sometimes the age of
    /// the person the page is about, which is why this one is withheld rather than treated
    /// as an opaque id that happens to be a link.
    /// </remarks>
    [Fact]
    public void Interpolating_a_finding_yields_no_listing_address()
    {
        var candidate = new SearchCandidate(
            new Uri("https://example-broker.test/profile/alex-whitfield-sacramento-ca-41"),
            [new FieldMatch(IdentityField.Names, MatchStrength.Exact)]);

        var interpolated = $"{candidate}";

        Assert.DoesNotContain("alex-whitfield", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("sacramento", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("example-broker.test", interpolated, StringComparison.Ordinal);
        Assert.Contains("withheld", interpolated, StringComparison.Ordinal);
        Assert.Contains("Matches = 1", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A result is what actually gets logged, and it says nothing about its findings.
    /// </summary>
    /// <remarks>
    /// It says nothing for a reason worth knowing rather than relying on: a record prints
    /// its members, and a member that is a list prints as the name of its type instead of
    /// its contents. So the withholding on a candidate is not what covers this case today —
    /// the collection is. Both are pinned here because the obvious improvement, when
    /// somebody finds a log line saying <c>List`1</c> and wants to know what was in it, is
    /// to spell the candidates out.
    /// </remarks>
    [Fact]
    public void A_result_discloses_nothing_about_the_findings_inside_it()
    {
        var found = new SearchResult.Found(
        [
            new SearchCandidate(
                new Uri("https://example-broker.test/profile/alex-whitfield-sacramento-ca-41"),
                [new FieldMatch(IdentityField.Names, MatchStrength.Exact)]),
        ]);

        var interpolated = $"{found}";

        Assert.DoesNotContain("alex-whitfield", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("example-broker.test", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a failure says is written for a log and goes into one.
    /// </summary>
    /// <remarks>
    /// The one string in these types that is printed verbatim. Nothing can stop a search
    /// putting a name in it, which is why the contract says what belongs there — this
    /// asserts the plumbing does not quietly withhold it instead, since a detail that never
    /// reaches the log is worse than one nobody reads.
    /// </remarks>
    [Fact]
    public void A_failure_detail_reaches_the_log_it_was_written_for()
    {
        var failed = new SearchResult.Failed(
            SearchFailureReason.PageShapeChanged,
            "no element matched .result-card",
            false);

        var interpolated = $"{failed}";

        Assert.Contains("no element matched .result-card", interpolated, StringComparison.Ordinal);
        Assert.Contains("PageShapeChanged", interpolated, StringComparison.Ordinal);
    }
}
