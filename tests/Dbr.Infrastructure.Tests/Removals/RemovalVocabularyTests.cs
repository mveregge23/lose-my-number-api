// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Removals;

namespace Dbr.Infrastructure.Tests.Removals;

/// <summary>
/// The spellings the check constraints, the value conversions and the JSON all share.
/// </summary>
public class RemovalVocabularyTests
{
    public static TheoryData<RemovalRequestStatus> RequestStatuses =>
        [.. Enum.GetValues<RemovalRequestStatus>()];

    public static TheoryData<RemovalJobStatus> JobStatuses => [.. Enum.GetValues<RemovalJobStatus>()];

    public static TheoryData<RemovalStrategy> Strategies => [.. Enum.GetValues<RemovalStrategy>()];

    public static TheoryData<DeadlineSource> DeadlineSources => [.. Enum.GetValues<DeadlineSource>()];

    [Theory]
    [MemberData(nameof(RequestStatuses))]
    public void Every_request_status_round_trips(RemovalRequestStatus status) =>
        Assert.Equal(status, RemovalVocabulary.ParseRequestStatus(RemovalVocabulary.ToWire(status)));

    [Theory]
    [MemberData(nameof(JobStatuses))]
    public void Every_job_status_round_trips(RemovalJobStatus status) =>
        Assert.Equal(status, RemovalVocabulary.ParseJobStatus(RemovalVocabulary.ToWire(status)));

    [Theory]
    [MemberData(nameof(Strategies))]
    public void Every_strategy_round_trips(RemovalStrategy strategy) =>
        Assert.Equal(strategy, RemovalVocabulary.ParseStrategy(RemovalVocabulary.ToWire(strategy)));

    [Theory]
    [MemberData(nameof(DeadlineSources))]
    public void Every_deadline_source_round_trips(DeadlineSource source) =>
        // Held back since DBR-024 on the grounds that a spelling pinned before a column
        // and a wire format existed would be a guess about two things at once. The column
        // exists now.
        Assert.Equal(source, CatalogVocabulary.ParseDeadlineSource(CatalogVocabulary.ToWire(source)));

    [Theory]
    [MemberData(nameof(RequestStatuses))]
    public void Request_statuses_are_spelled_the_way_the_constraint_expects(RemovalRequestStatus status) =>
        Assert.Matches("^[a-z_]+$", RemovalVocabulary.ToWire(status));

    [Fact]
    public void Multi_word_members_keep_their_underscores()
    {
        // The reason this vocabulary is spelled out rather than derived from member names.
        // Lower-casing would give 'requireshumaninput' and 'semiautomated', which no check
        // constraint accepts and no client sends.
        Assert.Equal("requires_human_input", RemovalVocabulary.ToWire(RemovalRequestStatus.RequiresHumanInput));
        Assert.Equal("awaiting_broker_response", RemovalVocabulary.ToWire(RemovalRequestStatus.AwaitingBrokerResponse));
        Assert.Equal("semi_automated", RemovalVocabulary.ToWire(RemovalStrategy.SemiAutomated));
        Assert.Equal("operational_default", CatalogVocabulary.ToWire(DeadlineSource.OperationalDefault));
    }

    [Fact]
    public void An_unrecognised_value_parses_to_null_rather_than_throwing()
    {
        Assert.Null(RemovalVocabulary.ParseRequestStatus("in_progress"));
        Assert.Null(RemovalVocabulary.ParseJobStatus(null));
        Assert.Null(RemovalVocabulary.ParseStrategy(string.Empty));
        Assert.Null(CatalogVocabulary.ParseDeadlineSource("statute"));
    }

    [Fact]
    public void An_unmapped_member_is_refused_on_the_way_to_storage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemovalVocabulary.ToWire((RemovalRequestStatus)999));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemovalVocabulary.ToWire((RemovalJobStatus)999));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemovalVocabulary.ToWire((RemovalStrategy)999));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CatalogVocabulary.ToWire((DeadlineSource)999));
    }
}
