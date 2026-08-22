// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;

namespace Dbr.Infrastructure.Tests.Monitoring;

/// <summary>
/// The spellings the check constraints, the value conversions and the JSON all share.
/// </summary>
/// <remarks>
/// Round-tripping every member matters more than it looks: the failure this catches is a
/// value added to an enum and not to the vocabulary, which throws on the first row that
/// carries it rather than at the point somebody wrote it.
/// </remarks>
public class MonitoringVocabularyTests
{
    public static TheoryData<ScanTrigger> Triggers => [.. Enum.GetValues<ScanTrigger>()];

    public static TheoryData<ScanStatus> ScanStatuses => [.. Enum.GetValues<ScanStatus>()];

    public static TheoryData<ExposureStatus> ExposureStatuses => [.. Enum.GetValues<ExposureStatus>()];

    [Theory]
    [MemberData(nameof(Triggers))]
    public void Every_trigger_round_trips(ScanTrigger trigger) =>
        Assert.Equal(trigger, MonitoringVocabulary.ParseScanTrigger(MonitoringVocabulary.ToWire(trigger)));

    [Theory]
    [MemberData(nameof(ScanStatuses))]
    public void Every_scan_status_round_trips(ScanStatus status) =>
        Assert.Equal(status, MonitoringVocabulary.ParseScanStatus(MonitoringVocabulary.ToWire(status)));

    [Theory]
    [MemberData(nameof(ExposureStatuses))]
    public void Every_exposure_status_round_trips(ExposureStatus status) =>
        Assert.Equal(status, MonitoringVocabulary.ParseExposureStatus(MonitoringVocabulary.ToWire(status)));

    [Theory]
    [MemberData(nameof(ScanStatuses))]
    public void Scan_statuses_are_spelled_the_way_the_constraint_expects(ScanStatus status) =>
        // Lower-case with no separators is what the check constraint lists. Asserted
        // against the shape rather than against a second copy of the list, so this does
        // not become the thing that has to agree with the vocabulary it is checking.
        Assert.Matches("^[a-z_]+$", MonitoringVocabulary.ToWire(status));

    [Fact]
    public void An_unrecognised_value_parses_to_null_rather_than_throwing()
    {
        // These arrive from a client far more often than from the database, and a caller
        // that needs it to be fatal says so at its own call site.
        Assert.Null(MonitoringVocabulary.ParseScanStatus("in_progress"));
        Assert.Null(MonitoringVocabulary.ParseExposureStatus(null));
        Assert.Null(MonitoringVocabulary.ParseScanTrigger(string.Empty));
    }

    [Fact]
    public void An_unmapped_member_is_refused_on_the_way_to_storage()
    {
        // The direction that must not be lenient: writing a value no constraint accepts
        // should fail where the value was produced, not as a 500 from the database.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MonitoringVocabulary.ToWire((ScanStatus)999));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MonitoringVocabulary.ToWire((ExposureStatus)999));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MonitoringVocabulary.ToWire((ScanTrigger)999));
    }
}
