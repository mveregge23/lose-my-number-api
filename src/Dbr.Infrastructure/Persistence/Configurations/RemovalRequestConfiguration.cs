// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Removals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="RemovalRequest"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// No relationships are declared. Both foreign keys to the exposure are over pairs — one
/// carrying the tenant, one carrying the broker — and EF would model them as navigations
/// generating joins that the query filter already constrains. The ids stand on their own;
/// the database is what holds them in agreement.
/// </remarks>
internal sealed class RemovalRequestConfiguration : IEntityTypeConfiguration<RemovalRequest>
{
    public void Configure(EntityTypeBuilder<RemovalRequest> builder)
    {
        builder.HasKey(request => request.Id);

        builder.Property(request => request.Status)
            .HasConversion(
                status => RemovalVocabulary.ToWire(status),
                stored => StatusFromStorage(stored));

        builder.Property(request => request.Strategy)
            .HasConversion(
                strategy => RemovalVocabulary.ToWire(strategy),
                stored => StrategyFromStorage(stored));

        builder.Property(request => request.DeadlineSource)
            .HasConversion(
                source => CatalogVocabulary.ToWire(source),
                stored => SourceFromStorage(stored));

        // The partial unique index on open requests per exposure is not declared: EF
        // cannot express the predicate, and a plain unique index here would be a claim
        // the database does not match — and one that would refuse a legitimate second
        // request after an earlier one expired.
    }

    private static RemovalRequestStatus StatusFromStorage(string stored) =>
        RemovalVocabulary.ParseRequestStatus(stored)
        ?? throw new InvalidOperationException(
            $"removal_request.status holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static RemovalStrategy StrategyFromStorage(string stored) =>
        RemovalVocabulary.ParseStrategy(stored)
        ?? throw new InvalidOperationException(
            $"removal_request.strategy holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static DeadlineSource SourceFromStorage(string stored) =>
        CatalogVocabulary.ParseDeadlineSource(stored)
        ?? throw new InvalidOperationException(
            $"removal_request.deadline_source holds '{stored}', which this build has no value "
            + "for. Either a migration widened the check constraint ahead of the code, or a "
            + "row was written by hand.");
}
