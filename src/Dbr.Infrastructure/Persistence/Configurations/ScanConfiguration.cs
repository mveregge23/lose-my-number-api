// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Scan"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// No relationship to <see cref="Dbr.Domain.Profiles.PrivacyProfile"/> is declared, and
/// the foreign key it would model is over a pair rather than a single column — EF would
/// have to be told about the tenant half as well, and would then generate joins carrying
/// a condition the query filter already applies. The id stands on its own; the database
/// is what enforces that it names a profile of this tenant's.
/// </remarks>
internal sealed class ScanConfiguration : IEntityTypeConfiguration<Scan>
{
    public void Configure(EntityTypeBuilder<Scan> builder)
    {
        builder.HasKey(scan => scan.Id);

        // The stored spelling and the one a client reads are the same spelling, kept in
        // one place so the check constraint, the conversion and the JSON cannot drift.
        builder.Property(scan => scan.Trigger)
            .HasConversion(
                trigger => MonitoringVocabulary.ToWire(trigger),
                stored => TriggerFromStorage(stored));

        builder.Property(scan => scan.Status)
            .HasConversion(
                status => MonitoringVocabulary.ToWire(status),
                stored => StatusFromStorage(stored));

        // The history index is not declared. EF generates no DDL here, and its ordering
        // is descending — which EF cannot express, so stating it ascending would put a
        // claim in the model that the database does not match.
    }

    private static ScanTrigger TriggerFromStorage(string stored) =>
        MonitoringVocabulary.ParseScanTrigger(stored)
        ?? throw new InvalidOperationException(
            $"scan.trigger holds '{stored}', which this build has no value for. Either a "
            + "migration widened the check constraint ahead of the code, or a row was "
            + "written by hand.");

    private static ScanStatus StatusFromStorage(string stored) =>
        MonitoringVocabulary.ParseScanStatus(stored)
        ?? throw new InvalidOperationException(
            $"scan.status holds '{stored}', which this build has no value for. Either a "
            + "migration widened the check constraint ahead of the code, or a row was "
            + "written by hand.");
}
