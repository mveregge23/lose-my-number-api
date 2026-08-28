// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Infrastructure.Tests.Profiles;

/// <summary>
/// The one spelling of each identity group, and the one it must not be confused with.
/// </summary>
/// <remarks>
/// Two different strings are derived from this enum and they have very different
/// consequences. This one goes in a check constraint, so changing it is a migration. The
/// cipher's binding interpolates the member name instead, so changing <em>that</em> makes
/// every existing ciphertext unreadable. The last test here is what stops the two being
/// treated as interchangeable.
/// </remarks>
public class IdentityVocabularyTests
{
    public static TheoryData<IdentityField> EveryField()
    {
        var data = new TheoryData<IdentityField>();

        foreach (var field in Enum.GetValues<IdentityField>())
        {
            data.Add(field);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryField))]
    public void Every_field_has_a_spelling_and_round_trips(IdentityField field)
    {
        Assert.Equal(field, IdentityVocabulary.Parse(IdentityVocabulary.ToWire(field)));
    }

    [Fact]
    public void No_two_fields_share_a_spelling()
    {
        var spellings = Enum.GetValues<IdentityField>().Select(IdentityVocabulary.ToWire).ToArray();

        Assert.Equal(spellings.Length, spellings.Distinct().Count());
    }

    [Theory]
    [InlineData("dob")]
    [InlineData("DateOfBirth")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_field(string? stored)
    {
        Assert.Null(IdentityVocabulary.Parse(stored));
    }

    [Fact]
    public void A_value_outside_the_enum_is_refused_rather_than_spelled()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityVocabulary.ToWire((IdentityField)99));
    }

    /// <summary>
    /// The stored spelling and the cipher's are deliberately different-looking.
    /// </summary>
    /// <remarks>
    /// If <c>ToWire</c> returned the member name, the column's vocabulary and the bytes
    /// authenticated into every ciphertext would be one string with two jobs, and a
    /// search-and-replace meaning to widen a check constraint would quietly orphan the
    /// vault. The date of birth is the case where they visibly differ, so it is the one
    /// worth pinning.
    /// </remarks>
    [Fact]
    public void The_stored_spelling_is_not_the_name_the_cipher_binds_to()
    {
        Assert.Equal("date_of_birth", IdentityVocabulary.ToWire(IdentityField.DateOfBirth));
        Assert.Equal("DateOfBirth", IdentityField.DateOfBirth.ToString());
    }
}
