// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PrivacyProfile"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// Only what the conventions cannot infer. The partial unique index that allows one
/// self profile per account is not declared: EF cannot express a filtered index, and a
/// plain unique index on tenant_id would state something the database does not enforce
/// and would forbid the managed identities the column exists to allow.
/// </remarks>
internal sealed class PrivacyProfileConfiguration : IEntityTypeConfiguration<PrivacyProfile>
{
    public void Configure(EntityTypeBuilder<PrivacyProfile> builder)
    {
        builder.HasKey(profile => profile.Id);

        // snake_case on the way in and parsed back on the way out, matching the check
        // constraint on the column. The general enum convention here lower-cases, which
        // would store 'authorizedother' against a constraint expecting
        // 'authorized_other' — so this one spells the mapping out rather than deriving
        // it, and the constraint rejects the write if the two ever disagree.
        builder.Property(profile => profile.RelationshipType)
            .HasConversion(
                relationship => ToStorage(relationship),
                stored => FromStorage(stored));
    }

    private static string ToStorage(ProfileRelationship relationship) => relationship switch
    {
        ProfileRelationship.Self => "self",
        ProfileRelationship.Dependent => "dependent",
        ProfileRelationship.AuthorizedOther => "authorized_other",
        _ => throw new ArgumentOutOfRangeException(
            nameof(relationship),
            relationship,
            "Unmapped relationship. Adding one means a migration widening the check "
            + "constraint on privacy_profile.relationship_type as well."),
    };

    private static ProfileRelationship FromStorage(string stored) => stored switch
    {
        "self" => ProfileRelationship.Self,
        "dependent" => ProfileRelationship.Dependent,
        "authorized_other" => ProfileRelationship.AuthorizedOther,
        _ => throw new InvalidOperationException(
            $"privacy_profile.relationship_type holds '{stored}', which this build has no "
            + "value for. Either a migration widened the constraint ahead of the code, or "
            + "a row was written by hand."),
    };
}
