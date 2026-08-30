// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ScanLeg"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The key is the scan and the broker, as it is on the narrowing table and for the same
/// reason: a scan id belongs to exactly one tenant, so adding the tenant would widen the
/// key without excluding anything while suggesting a company could legitimately have two
/// legs of one run.
/// </remarks>
internal sealed class ScanLegConfiguration : IEntityTypeConfiguration<ScanLeg>
{
    public void Configure(EntityTypeBuilder<ScanLeg> builder)
    {
        builder.HasKey(leg => new { leg.ScanId, leg.BrokerId });

        // Null stays null: a leg that has not finished has no outcome, and that absence is
        // what "is this run over" is a query for. A conversion mapping it to a word would
        // make the unfinished state a value somebody has to remember to exclude.
        builder.Property(leg => leg.Outcome)
            .HasConversion(
                outcome => outcome == null ? null : MonitoringVocabulary.ToWire(outcome.Value),
                stored => OutcomeFromStorage(stored));
    }

    private static ScanLegOutcome? OutcomeFromStorage(string? stored) =>
        stored is null
            ? null
            : MonitoringVocabulary.ParseScanLegOutcome(stored)
              ?? throw new InvalidOperationException(
                  $"scan_leg.outcome holds '{stored}', which this build has no value for. Either "
                  + "a migration widened the check constraint ahead of the code, or a row was "
                  + "written by hand.");
}
