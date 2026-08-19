// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="LegalBasis"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// Shared reference data, so no tenant filter — see <see cref="BrokerConfiguration"/>
/// for why that absence is deliberate rather than missed.
/// </remarks>
internal sealed class LegalBasisConfiguration : IEntityTypeConfiguration<LegalBasis>
{
    public void Configure(EntityTypeBuilder<LegalBasis> builder)
    {
        builder.HasKey(basis => basis.Id);

        // One spelling for the column, the conversion and the wire. Lower-casing the C#
        // member would give 'optoutsale', which the check constraint rejects.
        builder.Property(basis => basis.RequestType)
            .HasConversion(
                type => CatalogVocabulary.ToWire(type),
                stored => RequestTypeFromStorage(stored));

        builder.Property(basis => basis.VerificationLevel)
            .HasConversion(
                level => CatalogVocabulary.ToWire(level),
                stored => VerificationLevelFromStorage(stored));

        builder.Property(basis => basis.Source)
            .HasConversion(
                source => CatalogVocabulary.ToWire(source),
                stored => SourceFromStorage(stored));

        builder.Property(basis => basis.DeadlineUnit)
            .HasConversion(
                unit => CatalogVocabulary.ToWire(unit),
                stored => DeadlineUnitFromStorage(stored));
    }

    private static CatalogSource SourceFromStorage(string stored) =>
        CatalogVocabulary.ParseCatalogSource(stored)
        ?? throw new InvalidOperationException(
            $"legal_basis.source holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static DeadlineUnit DeadlineUnitFromStorage(string stored) =>
        CatalogVocabulary.ParseDeadlineUnit(stored)
        ?? throw new InvalidOperationException(
            $"legal_basis.deadline_unit holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static LegalRequestType RequestTypeFromStorage(string stored) =>
        CatalogVocabulary.ParseLegalRequestType(stored)
        ?? throw new InvalidOperationException(
            $"legal_basis.request_type holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static VerificationLevel VerificationLevelFromStorage(string stored) =>
        CatalogVocabulary.ParseVerificationLevel(stored)
        ?? throw new InvalidOperationException(
            $"legal_basis.verification_level holds '{stored}', which this build has no value "
            + "for. Either a migration widened the check constraint ahead of the code, or a "
            + "row was written by hand.");
}
