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

        // Spelled out rather than derived: lower-casing OptOutSale gives 'optoutsale'
        // and the constraint expects 'opt_out_sale'. The constraint rejects the write
        // if this and the column ever disagree.
        builder.Property(basis => basis.RequestType)
            .HasConversion(
                type => ToStorage(type),
                stored => FromStorage(stored));

        // This one lower-cases cleanly, so it goes through the general convention.
        builder.Property(basis => basis.VerificationLevel)
            .HasConversion(
                level => level.ToString().ToLowerInvariant(),
                stored => Enum.Parse<VerificationLevel>(stored, ignoreCase: true));
    }

    private static string ToStorage(LegalRequestType type) => type switch
    {
        LegalRequestType.Delete => "delete",
        LegalRequestType.OptOutSale => "opt_out_sale",
        LegalRequestType.OptOutTargetedAds => "opt_out_targeted_ads",
        _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "Unmapped request type. Adding one means a migration widening the check "
            + "constraint on legal_basis.request_type as well."),
    };

    private static LegalRequestType FromStorage(string stored) => stored switch
    {
        "delete" => LegalRequestType.Delete,
        "opt_out_sale" => LegalRequestType.OptOutSale,
        "opt_out_targeted_ads" => LegalRequestType.OptOutTargetedAds,
        _ => throw new InvalidOperationException(
            $"legal_basis.request_type holds '{stored}', which this build has no value "
            + "for. Either a migration widened the constraint ahead of the code, or a row "
            + "was written by hand."),
    };
}
