// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Vault.Configurations;

/// <summary>
/// Maps <see cref="ProfileIdentity"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The schema is named here and nowhere else in the model. Everything else in this
/// assembly maps to <c>public</c> by default, so the one line saying <c>vault</c> is
/// what puts this table on the other side of the boundary — and what makes an entity
/// that forgot it fail loudly against the drift test rather than quietly resolve to a
/// core table that does not exist.
/// </remarks>
internal sealed class ProfileIdentityConfiguration
    : IEntityTypeConfiguration<ProfileIdentity>, IVaultEntityConfiguration
{
    public void Configure(EntityTypeBuilder<ProfileIdentity> builder)
    {
        builder.ToTable("profile_identity", VaultSchema.Name);

        // The profile's id, not an identity of its own: the two halves of one profile
        // carry the same key on both sides of the boundary, which is what lets a caller
        // that resolved a profile through the core store ask for its fields without a
        // lookup table nobody could read anyway.
        builder.HasKey(identity => identity.PrivacyProfileId);

        builder.Property(identity => identity.PrivacyProfileId).ValueGeneratedNever();
    }
}
