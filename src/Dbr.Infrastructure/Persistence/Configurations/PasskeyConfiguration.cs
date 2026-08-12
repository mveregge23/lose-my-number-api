// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Passkey"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// Only what the conventions cannot infer. Table and column names come from the
/// snake_case convention, and the query filter narrowing this to one account is
/// applied by convention because the entity implements <c>ITenantScoped</c>.
/// <para>
/// The migration's two indexes are not declared here. EF generates no DDL in this
/// codebase and enforces neither uniqueness nor an index at runtime, so a declaration
/// would buy nothing and would carry a name of EF's choosing rather than the one the
/// database actually has.
/// </para>
/// </remarks>
internal sealed class PasskeyConfiguration : IEntityTypeConfiguration<Passkey>
{
    public void Configure(EntityTypeBuilder<Passkey> builder)
    {
        builder.HasKey(credential => credential.Id);

        // Declared for insert ordering, not for the constraint — the database already
        // has that. Signup writes the account and its first passkey in one
        // SaveChanges, and without a relationship to order them by, EF is free to
        // send the passkey first and have the database reject it for referring to an
        // account that does not exist yet.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(credential => credential.TenantId);
    }
}
