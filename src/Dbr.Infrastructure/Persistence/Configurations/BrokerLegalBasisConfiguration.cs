// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="BrokerLegalBasis"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The key is the pair, which is the whole shape of the thing: one confirmation per
/// broker and regime, and a second one is the same statement rather than a new fact.
/// </remarks>
internal sealed class BrokerLegalBasisConfiguration : IEntityTypeConfiguration<BrokerLegalBasis>
{
    public void Configure(EntityTypeBuilder<BrokerLegalBasis> builder)
    {
        builder.HasKey(confirmation => new { confirmation.BrokerId, confirmation.LegalBasisId });

        // No navigation properties to Broker or LegalBasis. Nothing has needed to walk
        // from a confirmation to either side yet, and the resolution this table exists
        // for is a join written where it happens — adding them now would be guessing at
        // a query shape and would give the model two ways to say the same thing.
    }
}
