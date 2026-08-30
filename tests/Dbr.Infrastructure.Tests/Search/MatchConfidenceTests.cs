// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Infrastructure.Tests.Search;

/// <summary>
/// What each combination of agreements is worth, and what clears the bar.
/// </summary>
/// <remarks>
/// The table below is the decision rather than a description of the arithmetic. Every row
/// is a finding somebody could be shown or not shown, so changing the weights or the curve
/// is meant to break these — that is what makes moving the bar a deliberate act rather than
/// a side effect of tuning a constant.
/// </remarks>
public class MatchConfidenceTests
{
    private const double Tolerance = 1e-9;

    private static FieldMatch Exact(IdentityField field) => new(field, MatchStrength.Exact);

    private static FieldMatch Partial(IdentityField field) => new(field, MatchStrength.Partial);

    private static FieldMatch Conflicting(IdentityField field) =>
        new(field, MatchStrength.Conflicting);

    /// <summary>
    /// The anchors: what a named finding is worth, spelled out rather than derived.
    /// </summary>
    public static TheoryData<FieldMatch[], double> Anchors => new()
    {
        // A name and nothing else — the finding this bar exists to hold back.
        { [Exact(IdentityField.Names)], 0.25 },

        // A name and a street address, both agreeing: the ordinary people-search result,
        // and the point the curve is set to call an even bet.
        { [Exact(IdentityField.Names), Exact(IdentityField.Addresses)], 0.5 },

        // One mailbox, one person.
        { [Exact(IdentityField.Contacts)], 0.5 },

        // A birthday on its own rules out a great many people and still leaves thousands.
        { [Exact(IdentityField.DateOfBirth)], 1.5 / 4.5 },

        // Everything this system holds, all agreeing — and still short of certainty.
        {
            [
                Exact(IdentityField.Names),
                Exact(IdentityField.Addresses),
                Exact(IdentityField.Contacts),
                Exact(IdentityField.DateOfBirth),
            ],
            7.5 / 10.5
        },
    };

    [Theory]
    [MemberData(nameof(Anchors))]
    public void A_named_finding_is_worth_what_it_is_worth(FieldMatch[] matches, double expected)
    {
        Assert.Equal(expected, MatchConfidence.Score(matches), Tolerance);
    }

    [Fact]
    public void A_name_on_its_own_is_not_shown_to_anybody()
    {
        var score = MatchConfidence.Score([Exact(IdentityField.Names)]);

        Assert.False(MatchConfidence.ClearsFloor(score));
    }

    [Theory]
    [InlineData(IdentityField.Addresses)]
    [InlineData(IdentityField.DateOfBirth)]
    public void A_name_and_any_second_agreement_clears_the_bar(IdentityField second)
    {
        var score = MatchConfidence.Score([Exact(IdentityField.Names), Partial(second)]);

        Assert.True(MatchConfidence.ClearsFloor(score));
    }

    [Fact]
    public void A_contradiction_costs_more_than_the_same_agreement_earns()
    {
        var agreed = MatchConfidence.Score(
            [Exact(IdentityField.Names), Partial(IdentityField.Addresses)]);

        var disagreed = MatchConfidence.Score(
            [Exact(IdentityField.Names), Conflicting(IdentityField.Addresses)]);

        Assert.True(disagreed < agreed);
        Assert.Equal(0.0, disagreed, Tolerance);
    }

    [Fact]
    public void A_listing_that_disagreed_with_more_than_it_agreed_with_is_no_evidence()
    {
        var score = MatchConfidence.Score(
            [Exact(IdentityField.Names), Conflicting(IdentityField.Contacts)]);

        Assert.Equal(0.0, score, Tolerance);
        Assert.False(MatchConfidence.ClearsFloor(score));
    }

    [Fact]
    public void A_mailbox_that_agrees_survives_an_address_that_does_not()
    {
        // People move, and the old address on a listing disagreeing with the current one
        // is the ordinary case rather than a different person.
        var score = MatchConfidence.Score(
            [
                Exact(IdentityField.Names),
                Exact(IdentityField.Contacts),
                Conflicting(IdentityField.Addresses),
            ]);

        Assert.True(MatchConfidence.ClearsFloor(score));
    }

    [Fact]
    public void Nothing_is_ever_certain()
    {
        var everything = Enum.GetValues<IdentityField>().Select(Exact).ToArray();

        Assert.True(MatchConfidence.Score(everything) < 1.0);
    }

    [Fact]
    public void No_evidence_is_no_confidence()
    {
        var score = MatchConfidence.Score([]);

        Assert.Equal(0.0, score, Tolerance);
        Assert.False(MatchConfidence.ClearsFloor(score));
    }

    [Fact]
    public void The_order_the_agreements_arrive_in_does_not_change_the_answer()
    {
        FieldMatch[] one =
        [
            Exact(IdentityField.Names),
            Partial(IdentityField.Addresses),
            Conflicting(IdentityField.DateOfBirth),
        ];

        var other = one.Reverse().ToArray();

        Assert.Equal(MatchConfidence.Score(one), MatchConfidence.Score(other), Tolerance);
    }

    [Fact]
    public void Agreeing_about_more_never_makes_a_finding_worth_less()
    {
        var alone = MatchConfidence.Score([Exact(IdentityField.Names)]);

        foreach (var field in Enum.GetValues<IdentityField>().Where(f => f != IdentityField.Names))
        {
            var withMore = MatchConfidence.Score([Exact(IdentityField.Names), Exact(field)]);

            Assert.True(withMore > alone, $"Agreeing on {field} as well lowered the score.");
        }
    }

    [Fact]
    public void The_bar_includes_the_score_that_sits_exactly_on_it()
    {
        Assert.True(MatchConfidence.ClearsFloor(MatchConfidence.Floor));
        Assert.False(MatchConfidence.ClearsFloor(Math.BitDecrement(MatchConfidence.Floor)));
    }

    /// <summary>
    /// Every group of an identity is priced, and every degree of agreement is.
    /// </summary>
    /// <remarks>
    /// The two <c>switch</c> statements behind the score throw on a value they do not know,
    /// which is what stops a fifth identity group from being silently worth nothing. These
    /// walk the enums so that adding one fails here rather than in production on the first
    /// listing that matched it.
    /// </remarks>
    [Fact]
    public void Every_group_and_every_degree_of_agreement_is_priced()
    {
        foreach (var field in Enum.GetValues<IdentityField>())
        {
            foreach (var strength in Enum.GetValues<MatchStrength>())
            {
                var score = MatchConfidence.Score([new FieldMatch(field, strength)]);

                Assert.InRange(score, 0.0, 1.0);
            }
        }
    }
}
