// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.Domain.Search;

namespace Dbr.Infrastructure.Tests.Search;

/// <summary>
/// That there are three outcomes, and that there is no way to add a fourth from outside.
/// </summary>
/// <remarks>
/// The closed hierarchy is what lets a worker switch over a result without a default case
/// standing for "something else happened". It holds because the base constructor is private
/// and only nested types can reach it — an arrangement that survives exactly as long as
/// nobody widens that constructor to be helpful, which is what these check.
/// </remarks>
public class SearchResultShapeTests
{
    private static readonly Type[] Outcomes =
        [.. typeof(SearchResult).Assembly
            .GetTypes()
            .Where(type => type != typeof(SearchResult) && typeof(SearchResult).IsAssignableFrom(type))];

    [Fact]
    public void There_are_exactly_three_outcomes()
    {
        Assert.Equal(
            ["Failed", "Found", "NothingFound"],
            Outcomes.Select(type => type.Name).Order().ToArray());
    }

    [Fact]
    public void Every_outcome_is_nested_in_the_result_it_is_a_case_of()
    {
        Assert.All(Outcomes, type => Assert.Equal(typeof(SearchResult), type.DeclaringType));
    }

    /// <summary>
    /// Sealed, so an outcome cannot be specialised into one nothing branches on.
    /// </summary>
    /// <remarks>
    /// A subclass of <c>Failed</c> would still match a <c>Failed</c> branch, which sounds
    /// safe and is how a case with meaning nobody handles gets in — it would carry
    /// something the switch never looks at while reading as a failure that was dealt with.
    /// </remarks>
    [Fact]
    public void Every_outcome_is_sealed()
    {
        Assert.All(Outcomes, type => Assert.True(type.IsSealed, type.Name));
    }

    /// <summary>
    /// The base cannot be constructed, so the list of outcomes cannot be extended.
    /// </summary>
    /// <remarks>
    /// Records get a protected copy constructor of their own, which is the compiler's and
    /// not a way in: it takes an existing instance, so it can only ever copy one of the
    /// three that already exist. Anything else that could be called from another assembly
    /// would open the hierarchy.
    /// </remarks>
    [Fact]
    public void Nothing_outside_this_type_can_construct_a_result()
    {
        var reachable = typeof(SearchResult)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor => !constructor.IsPrivate)
            .Where(constructor =>
                constructor.GetParameters() is not [{ ParameterType: var parameter }]
                || parameter != typeof(SearchResult))
            .ToArray();

        Assert.Empty(reachable);
    }
}
