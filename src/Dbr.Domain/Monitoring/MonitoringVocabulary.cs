// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>
/// The one spelling of each monitoring enum: what a column holds, and what a client
/// sends and reads back.
/// </summary>
/// <remarks>
/// The same arrangement the catalog vocabulary describes, and here for the same reason:
/// each of these strings appears in a check constraint, in the value conversion that
/// reads the column, and in the JSON on the wire, and a client filtering by a status it
/// was just handed should be sending the value the column is indexed on. Every member
/// here is a single word, so the lower-cased member name would have worked today — but
/// it would stop working the first time a two-word status is added, and it would stop
/// working silently, as a value no constraint accepts.
/// </remarks>
public static class MonitoringVocabulary
{
    public static string ToWire(ScanTrigger trigger) => trigger switch
    {
        ScanTrigger.Manual => "manual",
        ScanTrigger.Scheduled => "scheduled",
        _ => throw new ArgumentOutOfRangeException(
            nameof(trigger),
            trigger,
            "Unmapped scan trigger. Adding one means a migration widening the check "
            + "constraint on scan.trigger as well."),
    };

    public static ScanTrigger? ParseScanTrigger(string? value) => value switch
    {
        "manual" => ScanTrigger.Manual,
        "scheduled" => ScanTrigger.Scheduled,
        _ => null,
    };

    public static string ToWire(ScanStatus status) => status switch
    {
        ScanStatus.Queued => "queued",
        ScanStatus.Running => "running",
        ScanStatus.Completed => "completed",
        ScanStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Unmapped scan status. Adding one means a migration widening the check "
            + "constraint on scan.status as well."),
    };

    public static ScanStatus? ParseScanStatus(string? value) => value switch
    {
        "queued" => ScanStatus.Queued,
        "running" => ScanStatus.Running,
        "completed" => ScanStatus.Completed,
        "failed" => ScanStatus.Failed,
        _ => null,
    };

    public static string ToWire(ExposureStatus status) => status switch
    {
        ExposureStatus.New => "new",
        ExposureStatus.Requested => "requested",
        ExposureStatus.Removed => "removed",
        ExposureStatus.Reappeared => "reappeared",
        ExposureStatus.Dismissed => "dismissed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Unmapped exposure status. Adding one means a migration widening the check "
            + "constraint on exposure.status as well."),
    };

    public static ExposureStatus? ParseExposureStatus(string? value) => value switch
    {
        "new" => ExposureStatus.New,
        "requested" => ExposureStatus.Requested,
        "removed" => ExposureStatus.Removed,
        "reappeared" => ExposureStatus.Reappeared,
        "dismissed" => ExposureStatus.Dismissed,
        _ => null,
    };
}
