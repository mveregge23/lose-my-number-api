// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="RefreshToken"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The query filter narrowing this to one account is applied by convention, because
/// the entity implements <c>ITenantScoped</c>. As elsewhere, the migration's indexes
/// are not restated here: EF generates no DDL and enforces no uniqueness at runtime,
/// so a declaration would only introduce a name the database does not use.
/// </remarks>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(token => token.Id);

        // Declared for insert ordering rather than for the constraint, which the
        // database already has: signing up writes the account and its first session
        // together, and without a relationship to order by, EF may send the token
        // first and have it rejected for referring to an account that does not exist.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(token => token.TenantId);
    }
}
