// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Vault.Configurations;

/// <summary>
/// Maps <see cref="ExposureSource"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The schema is named here, as it is for the identity table beside it, and for the same
/// reason: everything else in this assembly maps to <c>public</c> by default, so the line
/// saying <c>vault</c> is what puts this on the other side of the boundary.
/// </remarks>
internal sealed class ExposureSourceConfiguration
    : IEntityTypeConfiguration<ExposureSource>, IVaultEntityConfiguration
{
    public void Configure(EntityTypeBuilder<ExposureSource> builder)
    {
        builder.ToTable("exposure_source", VaultSchema.Name);

        // The finding's id, not an identity of its own — the same key on both sides of the
        // boundary, so a caller that resolved an exposure through the core store can ask for
        // its source without a lookup table nothing could read anyway.
        builder.HasKey(source => source.ExposureId);

        builder.Property(source => source.ExposureId).ValueGeneratedNever();

        // No concurrency token, unlike the identity table. That one is read-modify-written
        // whenever somebody edits their profile, and two overlapping edits would silently
        // lose an address. This is written once when a finding is recorded and never
        // updated — a listing's address is not a thing that changes, and a finding whose
        // address did change would be a different finding.
    }
}
