// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;
using Dbr.Domain.Removals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="RemovalJob"/> onto the table its migration created.
/// </summary>
internal sealed class RemovalJobConfiguration : IEntityTypeConfiguration<RemovalJob>
{
    public void Configure(EntityTypeBuilder<RemovalJob> builder)
    {
        builder.HasKey(job => job.Id);

        builder.Property(job => job.Status)
            .HasConversion(
                status => RemovalVocabulary.ToWire(status),
                stored => StatusFromStorage(stored));

        // Null when the attempt ran, so the conversion has to survive a null rather than
        // parsing one. A nullable enum mapped through a non-nullable converter would turn
        // every successful attempt into a failure to read the column.
        builder.Property(job => job.FailureReason)
            .HasConversion(
                reason => reason == null ? null : RemovalVocabulary.ToWire(reason.Value),
                stored => stored == null ? null : ReasonFromStorage(stored));

        // No length on the detail, deliberately. The column is text with a check
        // constraint, which is how every bounded string in this schema is declared, and
        // saying HasMaxLength here would have the model expect a varchar — a disagreement
        // the drift test catches and a rename nobody made.

        // The unique constraint over (removal_request_id, attempt_number) is declared,
        // unlike most indexes here: EF can express it exactly, it is a plain unique
        // constraint with no predicate, and having it in the model means a duplicate
        // attempt fails in a test that never reaches Postgres.
        builder.HasIndex(job => new { job.RemovalRequestId, job.AttemptNumber }).IsUnique();
    }

    private static ConnectorFailureReason ReasonFromStorage(string stored) =>
        RemovalVocabulary.ParseFailureReason(stored)
        ?? throw new InvalidOperationException(
            $"removal_job.failure_reason holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");

    private static RemovalJobStatus StatusFromStorage(string stored) =>
        RemovalVocabulary.ParseJobStatus(stored)
        ?? throw new InvalidOperationException(
            $"removal_job.status holds '{stored}', which this build has no value for. Either "
            + "a migration widened the check constraint ahead of the code, or a row was "
            + "written by hand.");
}
