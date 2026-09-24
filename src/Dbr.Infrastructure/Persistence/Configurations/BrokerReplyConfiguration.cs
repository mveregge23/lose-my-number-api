// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Domain.Removals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="BrokerReply"/> onto the table its migration created.
/// </summary>
internal sealed class BrokerReplyConfiguration : IEntityTypeConfiguration<BrokerReply>
{
    public void Configure(EntityTypeBuilder<BrokerReply> builder)
    {
        builder.HasKey(reply => reply.Id);

        builder.Property(reply => reply.MatchedBy)
            .HasConversion(
                match => RemovalVocabulary.ToWire(match),
                stored => MatchFromStorage(stored));

        // Declared because EF can express it exactly — a plain unique constraint with no
        // predicate — which means a message filed twice fails in a test that never reaches
        // Postgres, and fails the same way there.
        builder.HasIndex(reply => new { reply.TenantId, reply.SourceRef }).IsUnique();
    }

    private static ReplyMatch MatchFromStorage(string stored) =>
        RemovalVocabulary.ParseReplyMatch(stored)
        ?? throw new InvalidOperationException(
            $"broker_reply.matched_by holds '{stored}', which this build has no value for. "
            + "Either a migration widened the check constraint ahead of the code, or a row "
            + "was written by hand.");
}
