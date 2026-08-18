// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Consent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ConsentRecord"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The unique index that makes the newest-row ordering total is not declared here. EF
/// cannot express its descending order, and stating it as an ascending one would
/// describe an index the database does not have — which is the exact thing the
/// schema-drift check exists to catch.
/// </remarks>
internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.HasKey(record => record.Id);

        // snake_case on the way in and parsed back on the way out, matching the check
        // constraint on the column. The general enum convention here lower-cases, which
        // would store 'autoremoval' against a constraint expecting 'auto_removal' — so
        // this spells the mapping out rather than deriving it, and the constraint
        // rejects the write if the two ever disagree.
        builder.Property(record => record.Scope)
            .HasConversion(
                scope => ToStorage(scope),
                stored => FromStorage(stored));
    }

    private static string ToStorage(ConsentScope scope) => scope switch
    {
        ConsentScope.Scan => "scan",
        ConsentScope.AutoRemoval => "auto_removal",
        ConsentScope.AutoResubmit => "auto_resubmit",
        _ => throw new ArgumentOutOfRangeException(
            nameof(scope),
            scope,
            "Unmapped consent scope. Adding one means a migration widening the check "
            + "constraint on consent_record.scope as well."),
    };

    private static ConsentScope FromStorage(string stored) => stored switch
    {
        "scan" => ConsentScope.Scan,
        "auto_removal" => ConsentScope.AutoRemoval,
        "auto_resubmit" => ConsentScope.AutoResubmit,
        _ => throw new InvalidOperationException(
            $"consent_record.scope holds '{stored}', which this build has no value for. "
            + "Either a migration widened the constraint ahead of the code, or a row was "
            + "written by hand."),
    };
}
