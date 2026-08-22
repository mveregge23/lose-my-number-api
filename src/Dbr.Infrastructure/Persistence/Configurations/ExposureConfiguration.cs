// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Exposure"/> onto the table its migration created.
/// </summary>
internal sealed class ExposureConfiguration : IEntityTypeConfiguration<Exposure>
{
    public void Configure(EntityTypeBuilder<Exposure> builder)
    {
        builder.HasKey(exposure => exposure.Id);

        builder.Property(exposure => exposure.Status)
            .HasConversion(
                status => MonitoringVocabulary.ToWire(status),
                stored => StatusFromStorage(stored));
    }

    private static ExposureStatus StatusFromStorage(string stored) =>
        MonitoringVocabulary.ParseExposureStatus(stored)
        ?? throw new InvalidOperationException(
            $"exposure.status holds '{stored}', which this build has no value for. Either "
            + "a migration widened the check constraint ahead of the code, or a row was "
            + "written by hand.");
}
