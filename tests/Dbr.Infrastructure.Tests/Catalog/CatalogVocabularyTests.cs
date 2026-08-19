// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Infrastructure.Tests.Catalog;

/// <summary>
/// The spelling shared by the column, the value conversion and the wire.
/// </summary>
/// <remarks>
/// Driven off the enum members rather than a list repeated here, which is the only way
/// these catch the thing worth catching: a value added to an enum and not given a
/// spelling. A hand-written list would pass, and the failure would arrive as a broker
/// row nothing could read.
/// </remarks>
public class CatalogVocabularyTests
{
    [Fact]
    public void Every_removal_method_has_a_spelling_that_survives_the_round_trip()
    {
        foreach (var method in Enum.GetValues<RemovalMethod>())
        {
            Assert.Equal(method, CatalogVocabulary.ParseRemovalMethod(CatalogVocabulary.ToWire(method)));
        }
    }

    [Fact]
    public void Every_contact_mode_has_a_spelling_that_survives_the_round_trip()
    {
        foreach (var mode in Enum.GetValues<EmailContactMode>())
        {
            Assert.Equal(mode, CatalogVocabulary.ParseEmailContactMode(CatalogVocabulary.ToWire(mode)));
        }
    }

    [Fact]
    public void Every_request_type_has_a_spelling_that_survives_the_round_trip()
    {
        foreach (var type in Enum.GetValues<LegalRequestType>())
        {
            Assert.Equal(type, CatalogVocabulary.ParseLegalRequestType(CatalogVocabulary.ToWire(type)));
        }
    }

    [Fact]
    public void Every_verification_level_has_a_spelling_that_survives_the_round_trip()
    {
        foreach (var level in Enum.GetValues<VerificationLevel>())
        {
            Assert.Equal(level, CatalogVocabulary.ParseVerificationLevel(CatalogVocabulary.ToWire(level)));
        }
    }

    [Fact]
    public void The_spellings_are_the_ones_the_check_constraints_accept()
    {
        // The values in the migration, written out once. If a spelling is ever derived
        // from a member name instead, this is what fails — lower-casing OptOutSale gives
        // 'optoutsale' and the column rejects it.
        Assert.Equal("webform", CatalogVocabulary.ToWire(RemovalMethod.WebForm));
        Assert.Equal("alias_preferred", CatalogVocabulary.ToWire(EmailContactMode.AliasPreferred));
        Assert.Equal("tenant_real_required", CatalogVocabulary.ToWire(EmailContactMode.TenantRealRequired));
        Assert.Equal("opt_out_sale", CatalogVocabulary.ToWire(LegalRequestType.OptOutSale));
        Assert.Equal("opt_out_targeted_ads", CatalogVocabulary.ToWire(LegalRequestType.OptOutTargetedAds));
        Assert.Equal("enhanced", CatalogVocabulary.ToWire(VerificationLevel.Enhanced));
    }

    [Fact]
    public void A_value_no_build_has_is_answered_rather_than_thrown()
    {
        // Parsing is where an unknown value arrives from a client, which is ordinary.
        // A caller that needs it to be fatal — reading a column, where an unknown value
        // means the schema moved ahead of the code — says so at its own call site.
        Assert.Null(CatalogVocabulary.ParseRemovalMethod("carrier-pigeon"));
        Assert.Null(CatalogVocabulary.ParseLegalRequestType(null));
        Assert.Null(CatalogVocabulary.ParseVerificationLevel(string.Empty));
        Assert.Null(CatalogVocabulary.ParseEmailContactMode("AliasPreferred"));
    }

    [Fact]
    public void A_member_with_no_spelling_is_a_loud_failure_rather_than_a_silent_one()
    {
        // Casting past the defined members is the only way to reach the default arm, and
        // it stands in for the real case: somebody adds a member and does not come here.
        // Throwing is right — the alternative is writing a value the column refuses.
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogVocabulary.ToWire((RemovalMethod)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogVocabulary.ToWire((EmailContactMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogVocabulary.ToWire((LegalRequestType)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogVocabulary.ToWire((VerificationLevel)99));
    }
}
