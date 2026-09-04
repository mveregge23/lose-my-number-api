// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Removals;

namespace Dbr.Infrastructure.Tests.Removals;

/// <summary>
/// How a demand gets carried out, and the one company shape this instance cannot help with.
/// </summary>
public class RemovalStrategiesTests
{
    [Theory]
    [InlineData(RemovalMethod.WebForm, RemovalStrategy.Automated)]
    [InlineData(RemovalMethod.Api, RemovalStrategy.Automated)]
    [InlineData(RemovalMethod.Email, RemovalStrategy.ManualEmail)]
    public void A_company_that_can_be_reached_gets_a_strategy(
        RemovalMethod method,
        RemovalStrategy expected)
    {
        Assert.Equal(expected, RemovalStrategies.ForMethod(method));
    }

    /// <summary>
    /// Post has no strategy, and is not quietly turned into the nearest one.
    /// </summary>
    /// <remarks>
    /// The nearest one is a message to an opt-out mailbox, and the company that publishes a
    /// postal address is doing so precisely because it does not take email — so the
    /// substitution fails for exactly the companies it would be applied to, and fails
    /// silently: the demand looks sent and its deadline runs.
    /// </remarks>
    [Fact]
    public void A_company_that_only_takes_paper_gets_none()
    {
        Assert.Null(RemovalStrategies.ForMethod(RemovalMethod.Postal));
    }

    /// <summary>
    /// Every method the catalog can hold has an answer here, one way or the other.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than listed, so that adding a method to the catalog fails this
    /// test rather than throwing the first time somebody opens a demand against a company
    /// that uses it.
    /// </remarks>
    [Fact]
    public void Every_method_the_catalog_knows_is_answered()
    {
        foreach (var method in Enum.GetValues<RemovalMethod>())
        {
            var strategy = RemovalStrategies.ForMethod(method);

            Assert.True(
                strategy is null || Enum.IsDefined(strategy.Value),
                $"{method} produced something that is not a strategy.");
        }
    }

    /// <summary>
    /// Nothing here produces the strategy that means "a person has to step in".
    /// </summary>
    /// <remarks>
    /// Asserted so the gap stays visible. The catalog says how a company accepts a demand
    /// and not whether a script can finish one, so every form broker starts out automated
    /// and a connector reaching a step it cannot pass is what discovers otherwise. If a
    /// catalog field for it ever arrives, this test is what fails and points at the
    /// mapping that has to change.
    /// </remarks>
    [Fact]
    public void No_method_yet_says_a_person_will_be_needed_partway()
    {
        var produced = Enum.GetValues<RemovalMethod>()
            .Select(RemovalStrategies.ForMethod)
            .ToArray();

        Assert.DoesNotContain(RemovalStrategy.SemiAutomated, produced);
    }
}
