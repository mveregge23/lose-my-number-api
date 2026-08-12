// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Tenant"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// Only what the conventions cannot infer. Table and column names come from the
/// snake_case convention, so spelling them here would be duplication that can go stale
/// without anything noticing.
/// </remarks>
internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(tenant => tenant.Id);

        // Lower-cased on the way in and parsed back on the way out, matching the
        // check constraint on the column. Storing the C# casing instead would work
        // until someone wrote a value by hand in psql.
        builder.Property(tenant => tenant.Status)
            .HasConversion(
                status => status.ToString().ToLowerInvariant(),
                stored => Enum.Parse<TenantStatus>(stored, ignoreCase: true));

        // No index declared for email. The real one is unique on lower(email), which
        // EF cannot express, and declaring a plain index on email instead would put a
        // statement in the model that is simply not true of the database. EF never
        // generates DDL here and does not enforce uniqueness at runtime, so the
        // declaration would buy nothing in exchange for being wrong.
    }
}
