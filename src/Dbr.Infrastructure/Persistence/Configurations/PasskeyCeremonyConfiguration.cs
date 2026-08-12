// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PasskeyCeremony"/> onto the table its migration created.
/// </summary>
internal sealed class PasskeyCeremonyConfiguration : IEntityTypeConfiguration<PasskeyCeremony>
{
    public void Configure(EntityTypeBuilder<PasskeyCeremony> builder)
    {
        builder.HasKey(ceremony => ceremony.Id);

        // Lower-cased on the way in and parsed back on the way out, matching the
        // check constraint on the column — the same treatment account status gets,
        // and for the same reason: the C# casing would work until someone wrote a
        // value by hand in psql.
        builder.Property(ceremony => ceremony.Purpose)
            .HasConversion(
                purpose => purpose.ToString().ToLowerInvariant(),
                stored => Enum.Parse<PasskeyCeremonyPurpose>(stored, ignoreCase: true));

        // jsonb, not text. The column holds a document, and typing it as one means
        // Postgres rejects a malformed write at the point it happens rather than
        // leaving a row that only fails when something tries to parse it back.
        builder.Property(ceremony => ceremony.Options).HasColumnType("jsonb");

        // No query filter, and no ITenantScoped. This table sits outside the tenant
        // boundary because a ceremony exists during the window where there is no
        // tenant to scope it to — see the entity for the whole of that argument. Its
        // migration deliberately does not call app.enable_tenant_rls.
    }
}
