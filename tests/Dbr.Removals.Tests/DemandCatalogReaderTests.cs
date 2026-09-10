// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Profiles;

namespace Dbr.Removals.Tests;

/// <summary>
/// The content that actually ships, read the way the worker reads it.
/// </summary>
/// <remarks>
/// Against the real catalog rather than a copy written for the test. A test that validated
/// its own fixture would pass while the wording that goes to a company was broken, which is
/// the failure this whole tier exists to make impossible.
/// </remarks>
public class DemandCatalogReaderTests
{
    private static DemandCatalogReadResult Shipped() => DemandCatalogReader.Read();

    [Fact]
    public void Everything_that_ships_is_readable()
    {
        var read = Shipped();

        Assert.Empty(read.Problems);
    }

    [Fact]
    public void The_reference_company_can_be_asked_by_mail()
    {
        var read = Shipped();

        var recipe = Assert.Single(read.Recipes);

        Assert.Equal("privacy", recipe.Mailbox);
        Assert.Equal("privacy@example.com", recipe.AddressAt("example.com"));
    }

    [Fact]
    public void The_wording_that_ships_covers_the_demands_it_claims_to()
    {
        var read = Shipped();

        var keys = read.Templates.Select(template => template.Key).ToHashSet();

        Assert.Contains(DemandTemplateKey.For("CCPA", LegalRequestType.Delete), keys);
        Assert.Contains(DemandTemplateKey.For("CCPA", LegalRequestType.OptOutSale), keys);
        Assert.Contains(DemandTemplateKey.Courtesy(LegalRequestType.Delete), keys);
    }

    [Fact]
    public void No_shipped_wording_asks_for_a_date_of_birth()
    {
        // Not a rule the reader enforces, and worth asserting anyway: what the wording writes
        // is what a demand causes to be decrypted, so a placeholder added here would widen
        // every grant this connector is minted — quietly, and in a document reviewed for its
        // sentences rather than for its consequences.
        foreach (var template in Shipped().Templates)
        {
            Assert.DoesNotContain(IdentityField.DateOfBirth, template.RequiredFields);
        }
    }

    [Fact]
    public void One_document_words_both_rights()
    {
        // Deletion and opting out of a sale are separate rights, and somebody who wants one
        // almost always wants the other — so the shipped wording exercises both and is filed
        // under each. The two keys must reach the same document, or a demand opened as an
        // opt-out would send different sentences from one opened as a deletion.
        var templates = Shipped().Templates;

        var deletion = templates.Single(t => t.Key == DemandTemplateKey.For("CCPA", LegalRequestType.Delete));
        var optOut = templates.Single(t => t.Key == DemandTemplateKey.For("CCPA", LegalRequestType.OptOutSale));

        Assert.Equal(deletion.Body.Raw, optOut.Body.Raw);
        Assert.Equal(deletion.Subject.Raw, optOut.Subject.Raw);

        // And it says so: both sections, in one message.
        Assert.Contains("1798.105", deletion.Body.Raw, StringComparison.Ordinal);
        Assert.Contains("1798.120", deletion.Body.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_wording_does_not_demand_publicly_available_information()
    {
        // Section 1798.140(v)(2) excludes it from the definition of personal information, and
        // much of what a data broker holds arrives that way. Demanding it claims a right the
        // act does not grant, and hands a company a correct-sounding reason to refuse all of
        // it rather than the part it covers.
        foreach (var template in Shipped().Templates)
        {
            Assert.DoesNotContain("public record", template.Body.Raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("publicly available", template.Body.Raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_document_naming_no_right_is_refused()
    {
        var root = WriteTemplate("reviewedBy: \"@somebody\"\nsubject: s\nbody: b\n");

        var read = DemandCatalogReader.Read(Path.Combine(root, "no-recipes"), root);

        Assert.Empty(read.Templates);
        Assert.Contains(
            read.Problems,
            problem => problem.Contains("does not say which rights it invokes", StringComparison.Ordinal));
    }

    [Fact]
    public void A_right_this_catalog_does_not_know_is_refused()
    {
        var root = WriteTemplate(
            "requestTypes: [delete, sell_my_soul]\nreviewedBy: \"@somebody\"\n"
            + "subject: s\nbody: b\n");

        var read = DemandCatalogReader.Read(Path.Combine(root, "no-recipes"), root);

        Assert.Empty(read.Templates);
        Assert.Contains(
            read.Problems,
            problem => problem.Contains("sell_my_soul", StringComparison.Ordinal));
    }

    [Fact]
    public void The_courtesy_wording_asserts_no_statute()
    {
        // The one property that makes it a separate document. A request wearing the grammar
        // of a legal demand would claim an obligation nothing established.
        var courtesy = Shipped()
            .Templates
            .Single(template => template.Key == DemandTemplateKey.Courtesy(LegalRequestType.Delete));

        Assert.Null(courtesy.Key.StatuteCode);
        Assert.DoesNotContain("CCPA", courtesy.Body.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_subject_line_names_anybody()
    {
        // A subject crosses every relay in the clear and lands in a phone notification, so
        // who a demand is about belongs in the body.
        foreach (var template in Shipped().Templates)
        {
            Assert.Empty(template.Subject.RequiredFields);
        }
    }

    [Theory]
    [InlineData("privacy@example.com", "writes a whole address")]
    [InlineData("privacy, evil@elsewhere.test", "writes a whole address")]
    [InlineData("privacy <evil@elsewhere.test>", "writes a whole address")]
    [InlineData("privacy, abuse", "punctuation no local part can carry")]
    [InlineData("privacy <abuse>", "punctuation no local part can carry")]
    public void A_recipe_cannot_send_a_demand_somewhere_nobody_reviewed(string mailbox, string expected)
    {
        // The refusal that is about what a contributed document must not be able to do. Each
        // of these reads as a changed string in a diff and would send somebody's name and home
        // address to a mailbox no reviewer looked at.
        var root = WriteRecipe($"brokerId: {Guid.NewGuid()}\nmailbox: \"{mailbox}\"\n");

        var read = DemandCatalogReader.Read(root, Path.Combine(root, "no-templates"));

        Assert.Empty(read.Recipes);
        Assert.Contains(read.Problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Wording_nobody_reviewed_is_refused()
    {
        var root = WriteTemplate(
            "statuteCode: CCPA\nrequestTypes: [delete]\ncitationUrl: https://example.test\n"
            + "subject: s\nbody: b\n");

        var read = DemandCatalogReader.Read(Path.Combine(root, "no-recipes"), root);

        Assert.Empty(read.Templates);
        Assert.Contains(read.Problems, problem => problem.Contains("names nobody who reviewed it", StringComparison.Ordinal));
    }

    [Fact]
    public void Wording_that_names_an_act_and_gives_nowhere_to_read_it_is_refused()
    {
        var root = WriteTemplate(
            "statuteCode: CCPA\nrequestTypes: [delete]\nreviewedBy: \"@somebody\"\n"
            + "subject: s\nbody: b\n");

        var read = DemandCatalogReader.Read(Path.Combine(root, "no-recipes"), root);

        Assert.Empty(read.Templates);
        Assert.Contains(read.Problems, problem => problem.Contains("nowhere to read it", StringComparison.Ordinal));
    }

    [Fact]
    public void Wording_with_a_placeholder_that_is_not_one_fails_review_rather_than_a_demand()
    {
        var root = WriteTemplate(
            "requestTypes: [delete]\nreviewedBy: \"@somebody\"\nsubject: s\n"
            + "body: \"{{names.middle}}\"\n");

        var read = DemandCatalogReader.Read(Path.Combine(root, "no-recipes"), root);

        Assert.Empty(read.Templates);
        Assert.Contains(read.Problems, problem => problem.Contains("names.middle", StringComparison.Ordinal));
    }

    private static string WriteRecipe(string yaml)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var company = Path.Combine(root, "a-company");

        Directory.CreateDirectory(company);
        File.WriteAllText(Path.Combine(company, "email.yaml"), yaml);

        return root;
    }

    private static string WriteTemplate(string yaml)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a-template.yaml"), yaml);

        return root;
    }
}
