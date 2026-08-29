// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="IdentityRelease"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The query filter narrowing this to one account is applied by convention, because the
/// entity implements <c>ITenantScoped</c>. As elsewhere the migration's indexes are not
/// restated: EF generates no DDL and enforces no uniqueness at runtime, so declaring them
/// would only introduce names the database does not use.
/// </remarks>
internal sealed class IdentityReleaseConfiguration : IEntityTypeConfiguration<IdentityRelease>
{
    public void Configure(EntityTypeBuilder<IdentityRelease> builder)
    {
        builder.HasKey(release => release.Id);

        // The stored spelling is the one place the check constraint and the model agree,
        // so it goes through the vocabulary rather than through the enum's own name — the
        // cipher already binds to that name, and the two must not be able to move
        // together.
        builder.Property(release => release.Fields)
            .HasConversion(
                fields => fields.Select(IdentityVocabulary.ToWire).ToArray(),
                stored => FromStorage(stored),
                // A list-valued property needs one, or EF compares by reference and
                // decides a grant is unchanged whatever it now holds. Nothing updates
                // this column — the role has no privilege to — so the comparer exists to
                // keep the change tracker honest rather than to make an update work.
                new ValueComparer<IReadOnlyList<IdentityField>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    value => value.Aggregate(0, (hash, field) => HashCode.Combine(hash, field)),
                    value => value.ToArray()));
    }

    private static IReadOnlyList<IdentityField> FromStorage(string[] stored) =>
        [.. stored.Select(value =>
            IdentityVocabulary.Parse(value)
            ?? throw new InvalidOperationException(
                $"identity_release.fields holds '{value}', which this build has no value "
                + "for. Either a migration widened the check constraint ahead of the "
                + "code, or a row was written by hand."))];
}
