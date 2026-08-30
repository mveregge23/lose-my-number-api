// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Search;
using Dbr.Infrastructure.Monitoring;

namespace Dbr.Infrastructure.Tests.Monitoring;

/// <summary>
/// The vocabulary a leg is recorded in, and the settings that decide when one is planned.
/// </summary>
/// <remarks>
/// The round-trip matters more than it looks: the spelling here is what a check constraint
/// accepts, so a value the code can write and the database cannot is a row that fails at
/// the moment a scan finishes rather than at the moment somebody adds an outcome.
/// </remarks>
public class ScanLegTests
{
    [Fact]
    public void Every_outcome_has_a_spelling_that_reads_back_as_itself()
    {
        foreach (var outcome in Enum.GetValues<ScanLegOutcome>())
        {
            var wire = MonitoringVocabulary.ToWire(outcome);

            Assert.Equal(outcome, MonitoringVocabulary.ParseScanLegOutcome(wire));
        }
    }

    [Fact]
    public void Two_outcomes_never_share_a_spelling()
    {
        var spellings = Enum.GetValues<ScanLegOutcome>()
            .Select(MonitoringVocabulary.ToWire)
            .ToList();

        Assert.Equal(spellings.Count, spellings.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Found")]
    [InlineData("nothingfound")]
    public void A_spelling_this_build_has_no_value_for_parses_to_nothing(string? stored)
    {
        Assert.Null(MonitoringVocabulary.ParseScanLegOutcome(stored));
    }

    /// <summary>
    /// Which outcomes mean the company was actually reached.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived, because this is the decision that separates a run
    /// that covered its brokers from one that did not — and every value not named here
    /// makes a scan fail, which is a claim worth stating rather than inferring.
    /// </remarks>
    [Fact]
    public void Only_an_answer_from_the_broker_counts_as_having_reached_it()
    {
        Assert.True(ScanLegOutcomes.IsAnswer(ScanLegOutcome.Found));
        Assert.True(ScanLegOutcomes.IsAnswer(ScanLegOutcome.NothingFound));

        var rest = Enum.GetValues<ScanLegOutcome>()
            .Where(outcome => outcome is not (ScanLegOutcome.Found or ScanLegOutcome.NothingFound));

        foreach (var outcome in rest)
        {
            Assert.False(ScanLegOutcomes.IsAnswer(outcome), $"{outcome} should not count as reached.");
        }
    }

    /// <summary>
    /// The grant a leg carries is a bearer credential, and a record prints every member.
    /// </summary>
    [Fact]
    public void The_work_in_a_lane_does_not_print_the_grant_it_carries()
    {
        var work = new ScanBrokerWork(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "a-grant-nobody-should-see",
            1);

        var printed = work.ToString();

        Assert.DoesNotContain("a-grant-nobody-should-see", printed, StringComparison.Ordinal);
        Assert.Contains("[withheld]", printed, StringComparison.Ordinal);

        // The ids are there, because they are what a log line is for.
        Assert.Contains(work.ScanId.ToString(), printed, StringComparison.Ordinal);
        Assert.Contains(work.BrokerId.ToString(), printed, StringComparison.Ordinal);
    }

    /// <summary>The lane a piece of scan work belongs in is the company's own.</summary>
    [Fact]
    public void Scan_work_is_addressed_to_its_company()
    {
        var brokerId = Guid.NewGuid();

        var work = new ScanBrokerWork(
            Guid.NewGuid(),
            Guid.NewGuid(),
            brokerId,
            Guid.NewGuid(),
            "grant",
            1);

        Assert.Equal(brokerId, work.BrokerId);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(15, 0)]
    [InlineData(15, -5)]
    public void A_dispatcher_that_could_not_work_refuses_to_start(int pollSeconds, int batchSize)
    {
        var options = new ScanDispatchOptions
        {
            Enabled = true,
            PollSeconds = pollSeconds,
            BatchSize = batchSize,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Settings_that_could_not_work_are_not_checked_when_nothing_will_use_them()
    {
        var options = new ScanDispatchOptions { Enabled = false, PollSeconds = 0, BatchSize = 0 };

        options.Validate();
    }

    /// <summary>
    /// The default is on, which is the point of the setting existing at all.
    /// </summary>
    /// <remarks>
    /// A scan somebody asked for and that nothing ever starts is the failure this whole
    /// arrangement removes, so reintroducing it should take a deliberate act rather than a
    /// missing line of configuration.
    /// </remarks>
    [Fact]
    public void A_deployment_that_configured_nothing_still_starts_the_scans_it_is_asked_for()
    {
        var options = new ScanDispatchOptions();

        options.Validate();

        Assert.True(options.Enabled);
    }

    /// <summary>Nothing in this build knows how to search anybody yet, and says so.</summary>
    [Fact]
    public void A_build_with_no_searches_answers_that_it_has_none()
    {
        IBrokerSearchRegistry registry = new Infrastructure.Search.EmptyBrokerSearchRegistry();

        Assert.Null(registry.Find(Guid.NewGuid()));
    }
}
