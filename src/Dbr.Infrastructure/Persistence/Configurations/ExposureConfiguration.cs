// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

        builder.Property(exposure => exposure.AgreedNames).HasConversion(Strength());
        builder.Property(exposure => exposure.AgreedAddresses).HasConversion(Strength());
        builder.Property(exposure => exposure.AgreedContacts).HasConversion(Strength());
        builder.Property(exposure => exposure.AgreedDateOfBirth).HasConversion(Strength());

        builder.Ignore(exposure => exposure.Agreement);
        builder.Ignore(exposure => exposure.RecordsAgreement);
    }

    private static ValueConverter<MatchStrength?, string?> Strength() =>
        new(
            strength => strength.HasValue ? IdentityVocabulary.ToWire(strength.Value) : null,
            stored => stored == null ? null : StrengthFromStorage(stored));

    private static MatchStrength StrengthFromStorage(string stored) =>
        IdentityVocabulary.ParseStrength(stored)
        ?? throw new InvalidOperationException(
            $"an exposure agreement column holds '{stored}', which this build has no value "
            + "for. Either a migration widened the check constraint ahead of the code, or a "
            + "row was written by hand.");

    private static ExposureStatus StatusFromStorage(string stored) =>
        MonitoringVocabulary.ParseExposureStatus(stored)
        ?? throw new InvalidOperationException(
            $"exposure.status holds '{stored}', which this build has no value for. Either "
            + "a migration widened the check constraint ahead of the code, or a row was "
            + "written by hand.");
}
