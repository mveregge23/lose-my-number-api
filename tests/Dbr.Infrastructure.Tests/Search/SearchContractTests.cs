// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Infrastructure.Tests.Search;

/// <summary>
/// The rules a search and its caller have to keep, and what each one refuses.
/// </summary>
/// <remarks>
/// Every case here is something a contributed search could do by accident. The two that
/// matter most are the ones about fields it never declared: a context carrying more of an
/// identity than was asked for means something has already been decrypted that should not
/// have been, and a finding claiming a match on a field the search never held is a claim it
/// could not have been in a position to make. Both would pass any test that only checked
/// the shape of the types.
/// </remarks>
public class SearchContractTests
{
    private static readonly Uri Listing = new("https://example-broker.test/profile/1");

    private static SearchCapabilities Needs(params IdentityField[] fields) =>
        new(SearchKind.Recipe, fields.ToHashSet());

    private static ProfileIdentityFields Identity(
        IReadOnlyList<string>? names = null,
        IReadOnlyList<ProfileAddress>? addresses = null,
        IReadOnlyList<ProfileContact>? contacts = null,
        DateOnly? dateOfBirth = null) =>
        new(names ?? [], addresses ?? [], contacts ?? [], dateOfBirth);

    private static SearchContext Context(ProfileIdentityFields? identity = null) =>
        new(
            Guid.NewGuid(),
            new SearchTarget(Guid.NewGuid(), "example-broker.test"),
            identity ?? Identity(names: ["Alex Whitfield"]),
            1);

    private static ProfileAddress AnAddress() =>
        new(Guid.NewGuid(), "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US");

    private static ProfileContact AContact() =>
        new(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test");

    private static SearchResult.Found FoundOn(params FieldMatch[] matches) =>
        new([new SearchCandidate(Listing, matches)]);

    [Fact]
    public void A_context_that_carries_what_was_declared_is_allowed()
    {
        Assert.Null(SearchContract.Refuse(Needs(IdentityField.Names), Context()));
    }

    [Fact]
    public void A_search_that_needs_no_field_is_refused()
    {
        var refusal = SearchContract.Refuse(
            new SearchCapabilities(SearchKind.Code, new HashSet<IdentityField>()),
            Context());

        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_context_naming_no_scan_is_refused()
    {
        var context = Context() with { ScanId = Guid.Empty };

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_context_naming_no_broker_is_refused()
    {
        var context = Context() with { Broker = new SearchTarget(Guid.Empty, "example-broker.test") };

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_context_with_no_site_to_look_at_is_refused(string domain)
    {
        var context = Context() with { Broker = new SearchTarget(Guid.NewGuid(), domain) };

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Attempts_are_counted_from_one(int attempt)
    {
        var context = Context() with { AttemptNumber = attempt };

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), context));
    }

    /// <summary>
    /// The release handed over something the search never asked for.
    /// </summary>
    /// <remarks>
    /// Refused rather than trimmed. By the time this is visible the value has already left
    /// the vault, so dropping it here would hide a fault in whatever built the release
    /// while doing nothing about the decryption that already happened.
    /// </remarks>
    [Fact]
    public void A_context_carrying_an_undeclared_field_is_refused()
    {
        var context = Context(Identity(names: ["Alex Whitfield"], dateOfBirth: new DateOnly(1985, 4, 17)));

        var refusal = SearchContract.Refuse(Needs(IdentityField.Names), context);

        Assert.NotNull(refusal);
        Assert.Contains("date of birth", refusal, StringComparison.Ordinal);
    }

    public static TheoryData<ProfileIdentityFields, string> OverReleases() => new()
    {
        { Identity(names: ["Alex Whitfield"], addresses: [AnAddress()]), "addresses" },
        { Identity(names: ["Alex Whitfield"], contacts: [AContact()]), "contacts" },
        { Identity(names: ["Alex Whitfield"], dateOfBirth: new DateOnly(1985, 4, 17)), "date of birth" },
    };

    [Theory]
    [MemberData(nameof(OverReleases))]
    public void Every_group_beyond_the_declaration_is_named(ProfileIdentityFields identity, string named)
    {
        var refusal = SearchContract.Refuse(Needs(IdentityField.Names), Context(identity));

        Assert.NotNull(refusal);
        Assert.Contains(named, refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the check cannot see, stated as a test so it stays stated.
    /// </summary>
    /// <remarks>
    /// An undeclared group that arrived empty is indistinguishable from one the profile
    /// simply has nothing in, and neither of them released anything. Asserting the allow
    /// here is what keeps somebody from later reading the rule as stricter than it is.
    /// </remarks>
    [Fact]
    public void An_empty_undeclared_group_released_nothing_and_is_allowed()
    {
        var context = Context(Identity(names: ["Alex Whitfield"], addresses: [], contacts: []));

        Assert.Null(SearchContract.Refuse(Needs(IdentityField.Names), context));
    }

    [Fact]
    public void A_declared_field_the_profile_has_nothing_in_is_allowed()
    {
        var context = Context(Identity(names: ["Alex Whitfield"]));

        Assert.Null(SearchContract.Refuse(
            Needs(IdentityField.Names, IdentityField.Addresses),
            context));
    }

    [Fact]
    public void Nothing_found_is_an_answer()
    {
        Assert.Null(SearchContract.Refuse(Needs(IdentityField.Names), new SearchResult.NothingFound()));
    }

    [Fact]
    public void A_failure_that_says_what_happened_is_allowed()
    {
        var failed = new SearchResult.Failed(SearchFailureReason.Transient, "504 from the origin", true);

        Assert.Null(SearchContract.Refuse(Needs(IdentityField.Names), failed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_failure_with_no_detail_is_refused(string detail)
    {
        var failed = new SearchResult.Failed(SearchFailureReason.Blocked, detail, false);

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), failed));
    }

    [Fact]
    public void Findings_with_a_reason_to_believe_them_are_allowed()
    {
        var found = FoundOn(new FieldMatch(IdentityField.Names, MatchStrength.Exact));

        Assert.Null(SearchContract.Refuse(Needs(IdentityField.Names), found));
    }

    [Fact]
    public void Findings_that_list_nothing_are_refused()
    {
        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), new SearchResult.Found([])));
    }

    [Theory]
    [InlineData("/profile/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html,hello")]
    public void A_finding_that_does_not_point_at_a_page_is_refused(string sourceRef)
    {
        var candidate = new SearchCandidate(
            new Uri(sourceRef, UriKind.RelativeOrAbsolute),
            [new FieldMatch(IdentityField.Names, MatchStrength.Exact)]);

        Assert.NotNull(SearchContract.Refuse(
            Needs(IdentityField.Names),
            new SearchResult.Found([candidate])));
    }

    [Fact]
    public void A_finding_with_nothing_behind_it_is_refused()
    {
        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), FoundOn()));
    }

    [Fact]
    public void A_finding_claiming_a_field_the_search_never_held_is_refused()
    {
        var found = FoundOn(
            new FieldMatch(IdentityField.Names, MatchStrength.Exact),
            new FieldMatch(IdentityField.DateOfBirth, MatchStrength.Exact));

        var refusal = SearchContract.Refuse(Needs(IdentityField.Names), found);

        Assert.NotNull(refusal);
        Assert.Contains("date of birth", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_answering_twice_for_one_field_is_refused()
    {
        var found = FoundOn(
            new FieldMatch(IdentityField.Names, MatchStrength.Exact),
            new FieldMatch(IdentityField.Names, MatchStrength.Conflicting));

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), found));
    }

    [Fact]
    public void A_finding_that_is_only_contradictions_is_refused()
    {
        var found = FoundOn(
            new FieldMatch(IdentityField.Names, MatchStrength.Conflicting),
            new FieldMatch(IdentityField.Addresses, MatchStrength.Conflicting));

        Assert.NotNull(SearchContract.Refuse(
            Needs(IdentityField.Names, IdentityField.Addresses),
            found));
    }

    /// <summary>
    /// Disagreement beside agreement is ordinary and stays reportable.
    /// </summary>
    /// <remarks>
    /// The same name at a different address is exactly the candidate the strength values
    /// exist to describe. Refusing it would leave a search with no way to report a doubt it
    /// actually had, which pushes it towards reporting the match alone.
    /// </remarks>
    [Fact]
    public void A_finding_that_agrees_somewhere_and_disagrees_elsewhere_is_allowed()
    {
        var found = FoundOn(
            new FieldMatch(IdentityField.Names, MatchStrength.Exact),
            new FieldMatch(IdentityField.Addresses, MatchStrength.Conflicting));

        Assert.Null(SearchContract.Refuse(
            Needs(IdentityField.Names, IdentityField.Addresses),
            found));
    }

    [Fact]
    public void One_listing_reported_twice_is_refused()
    {
        var match = new FieldMatch(IdentityField.Names, MatchStrength.Exact);

        var found = new SearchResult.Found(
        [
            new SearchCandidate(Listing, [match]),
            new SearchCandidate(Listing, [match]),
        ]);

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), found));
    }

    /// <summary>
    /// Two references to one page, differing only in where they scroll to.
    /// </summary>
    /// <remarks>
    /// Refused as the duplicate it is. A fragment is a position within a document rather
    /// than an address for a different one, so two of these are one listing counted twice
    /// — which is the thing worth catching, since a broker page that anchors each section
    /// would otherwise turn one finding into several.
    /// </remarks>
    [Fact]
    public void Two_references_to_one_page_are_one_listing()
    {
        var match = new FieldMatch(IdentityField.Names, MatchStrength.Exact);

        var found = new SearchResult.Found(
        [
            new SearchCandidate(new Uri("https://example-broker.test/profile/1#name"), [match]),
            new SearchCandidate(new Uri("https://example-broker.test/profile/1#address"), [match]),
        ]);

        Assert.NotNull(SearchContract.Refuse(Needs(IdentityField.Names), found));
    }

    [Fact]
    public void Two_listings_on_one_broker_are_two_findings()
    {
        var match = new FieldMatch(IdentityField.Names, MatchStrength.Exact);

        var found = new SearchResult.Found(
        [
            new SearchCandidate(new Uri("https://example-broker.test/profile/1"), [match]),
            new SearchCandidate(new Uri("https://example-broker.test/profile/2"), [match]),
        ]);

        Assert.Null(SearchContract.Refuse(Needs(IdentityField.Names), found));
    }
}
