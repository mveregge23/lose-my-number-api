// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Broker"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// No query filter, and that absence is the interesting part. The convention that
/// applies one does so for entities that carry a tenant; this one does not, so it is
/// left alone and every tenant reads every row. A filter here would empty the catalog
/// for everybody without anything failing.
/// </remarks>
internal sealed class BrokerConfiguration : IEntityTypeConfiguration<Broker>
{
    public void Configure(EntityTypeBuilder<Broker> builder)
    {
        builder.HasKey(broker => broker.Id);

        // The stored spelling and the one a client sends are the same spelling, kept in
        // one place so the column, the conversion and the JSON cannot drift apart.
        builder.Property(broker => broker.RemovalMethod)
            .HasConversion(
                method => CatalogVocabulary.ToWire(method),
                stored => FromStorage(stored));

        builder.Property(broker => broker.EmailContactMode)
            .HasConversion(
                mode => CatalogVocabulary.ToWire(mode),
                stored => ContactModeFromStorage(stored));

        // The unique index on domain and the partial index on active are not declared.
        // EF generates no DDL here, so a declaration would only be a claim about the
        // database — and the partial one is a claim EF cannot express correctly anyway.
    }

    private static RemovalMethod FromStorage(string stored) =>
        CatalogVocabulary.ParseRemovalMethod(stored)
        ?? throw new InvalidOperationException(
            $"broker.removal_method holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static EmailContactMode ContactModeFromStorage(string stored) =>
        CatalogVocabulary.ParseEmailContactMode(stored)
        ?? throw new InvalidOperationException(
            $"broker.email_contact_mode holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");
}
