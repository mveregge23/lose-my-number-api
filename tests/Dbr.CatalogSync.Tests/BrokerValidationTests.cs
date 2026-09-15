// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.CatalogSync;
using Dbr.Domain.Catalog;

namespace Dbr.CatalogSync.Tests;

/// <summary>
/// What the company reader refuses.
/// </summary>
/// <remarks>
/// <para>
/// As with the regimes, the cases are the ones that read fine in a pull request. A domain
/// with a scheme on it looks like a URL and is one; a recipe naming the wrong id is a
/// valid uuid either way; a company that takes mail with no mailbox beside it is two
/// files that are each correct. Every one of them is a company that silently does nothing.
/// </para>
/// <para>
/// A company is a directory, so these hand the reader paths rather than names.
/// </para>
/// </remarks>
public class BrokerValidationTests
{
    private const string Id = "6a1d7c2e-5b3f-4a8e-9c0d-1e2f3a4b5c6d";
    private const string OtherId = "0f9e8d7c-6b5a-4493-8271-605f4e3d2c1b";

    private const string Row = $"""
        id: {Id}
        name: Some Company
        domain: some-company.com
        removalMethod: email
        slaDays: 45
        sourceUrl: https://some-company.com/privacy
        """;

    private const string Mailbox = $"""
        brokerId: {Id}
        mailbox: privacy
        """;

    private const string Search = $$$"""
        brokerId: {{{Id}}}
        query: "/search?name={{names.full}}"
        item: ".result"
        """;

    private static BrokerReadResult Read(params (string Path, string Yaml)[] files) =>
        BrokerReader.Read(files);

    [Fact]
    public void A_well_formed_company_produces_its_row_with_the_slowest_pacing()
    {
        var read = Read(("some-company/broker.yaml", Row), ("some-company/email.yaml", Mailbox));

        Assert.Empty(read.Problems);
        var row = Assert.Single(read.Rows);
        Assert.Equal(Guid.Parse(Id), row.Id);
        Assert.Equal("some-company.com", row.Domain);
        Assert.Equal(RemovalMethod.Email, row.RemovalMethod);
        Assert.True(row.Active);
        Assert.Equal(EmailContactMode.AliasPreferred, row.EmailContactMode);

        // Nothing said about pacing means the gentlest lane there is, not a null.
        Assert.Equal(1, row.MaxConcurrency);
        Assert.Equal(1000, row.MinDelayMs);
        Assert.Equal(3, row.RateLimitThreshold);
        Assert.Equal(30, row.CooldownMinutes);
        Assert.Equal(3, row.FormChangeThreshold);
    }

    [Fact]
    public void A_domain_written_as_a_url_is_refused()
    {
        // The one that reads most naturally in a diff, and the one that would put
        // "https://https://" into every search and "@https://" into every address.
        var read = Read(
            ("some-company/broker.yaml", Row.Replace("some-company.com", "https://some-company.com", StringComparison.Ordinal)),
            ("some-company/email.yaml", Mailbox));

        Assert.Empty(read.Rows);
        Assert.Contains(read.Problems, problem => problem.Contains("bare host", StringComparison.Ordinal));
    }

    [Fact]
    public void A_domain_is_lowercased_rather_than_refused()
    {
        var read = Read(
            ("some-company/broker.yaml", Row.Replace("some-company.com", "Some-Company.COM", StringComparison.Ordinal)),
            ("some-company/email.yaml", Mailbox));

        Assert.Empty(read.Problems);
        Assert.Equal("some-company.com", Assert.Single(read.Rows).Domain);
    }

    [Fact]
    public void A_company_that_takes_mail_with_no_mailbox_beside_it_is_refused()
    {
        // Both files that exist are fine. What is wrong is the one that does not: a demand
        // to this company would be accepted and sit queued with nowhere to go.
        var read = Read(("some-company/broker.yaml", Row));

        Assert.Empty(read.Rows);
        var problem = Assert.Single(read.Problems);
        Assert.Contains("email.yaml", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_company_that_takes_a_form_needs_no_mailbox()
    {
        var read = Read(
            ("some-company/broker.yaml", Row.Replace("removalMethod: email", "removalMethod: webform", StringComparison.Ordinal)));

        Assert.Empty(read.Problems);
        Assert.Single(read.Rows);
    }

    [Fact]
    public void A_recipe_naming_a_different_company_than_the_one_beside_it_is_refused()
    {
        // A valid uuid either way, and a reviewer comparing two thirty-six character
        // strings by eye is the check this replaces.
        var read = Read(
            ("some-company/broker.yaml", Row),
            ("some-company/email.yaml", Mailbox),
            ("some-company/search.yaml", Search.Replace(Id, OtherId, StringComparison.Ordinal)));

        Assert.Empty(read.Rows);
        var problem = Assert.Single(read.Problems);
        Assert.Contains("search.yaml", problem, StringComparison.Ordinal);
        Assert.Contains(OtherId, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipes_with_no_company_beside_them_are_refused()
    {
        // The directory the tests used to be the only kind of: recipes bound to an id
        // that nothing would ever insert.
        var read = Read(("some-company/email.yaml", Mailbox), ("some-company/search.yaml", Search));

        Assert.Empty(read.Rows);
        var problem = Assert.Single(read.Problems);
        Assert.Contains("no broker.yaml", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_companies_under_one_id_are_refused()
    {
        var read = Read(
            ("one/broker.yaml", Row.Replace("some-company.com", "one.com", StringComparison.Ordinal)),
            ("one/email.yaml", Mailbox),
            ("two/broker.yaml", Row.Replace("some-company.com", "two.com", StringComparison.Ordinal)),
            ("two/email.yaml", Mailbox));

        Assert.Contains(read.Problems, problem => problem.Contains("id of more than one", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_companies_under_one_domain_are_refused()
    {
        var read = Read(
            ("one/broker.yaml", Row),
            ("one/email.yaml", Mailbox),
            ("two/broker.yaml", Row.Replace(Id, OtherId, StringComparison.Ordinal)),
            ("two/email.yaml", Mailbox.Replace(Id, OtherId, StringComparison.Ordinal)));

        Assert.Contains(read.Problems, problem => problem.Contains("domain of more than one", StringComparison.Ordinal));
    }

    [Fact]
    public void A_company_under_a_reserved_domain_is_a_worked_example_and_is_not_applied()
    {
        // Validated like the rest — a broken example is a broken template — and kept out
        // of the rows, so no instance ever paces a lane for it.
        var read = Read(
            ("example/broker.yaml", Row.Replace("some-company.com", "some-company.example", StringComparison.Ordinal)),
            ("example/email.yaml", Mailbox));

        Assert.Empty(read.Problems);
        Assert.Empty(read.Rows);
        Assert.Single(read.WorkedExamples);
    }

    [Fact]
    public void A_broken_worked_example_is_still_a_problem()
    {
        var read = Read(
            ("example/broker.yaml", Row
                .Replace("some-company.com", "some-company.example", StringComparison.Ordinal)
                .Replace("slaDays: 45", "slaDays: 0", StringComparison.Ordinal)),
            ("example/email.yaml", Mailbox));

        Assert.NotEmpty(read.Problems);
        Assert.Empty(read.WorkedExamples);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("listings.example.org")]
    [InlineData("acceptance-1234.test")]
    [InlineData("some-company.example")]
    [InlineData("broker.invalid")]
    [InlineData("localhost")]
    public void The_reserved_names_are_the_ones_the_internet_reserves(string domain) =>
        Assert.True(BrokerReader.IsReserved(domain));

    [Theory]
    [InlineData("examplebroker.com")]
    [InlineData("test.com")]
    [InlineData("example.co")]
    [InlineData("my-example.net.au")]
    public void A_real_name_that_merely_contains_a_reserved_word_is_not_reserved(string domain) =>
        Assert.False(BrokerReader.IsReserved(domain));

    [Fact]
    public void A_source_that_is_not_a_link_is_refused()
    {
        var read = Read(
            ("some-company/broker.yaml", Row.Replace("https://some-company.com/privacy", "their privacy page", StringComparison.Ordinal)),
            ("some-company/email.yaml", Mailbox));

        Assert.Empty(read.Rows);
        Assert.Contains(read.Problems, problem => problem.Contains("sourceUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Pacing_that_would_stop_the_lane_is_refused()
    {
        var read = Read(
            ("some-company/broker.yaml", Row + "\npacing:\n  maxConcurrency: 0\n  cooldownMinutes: 0\n"),
            ("some-company/email.yaml", Mailbox));

        Assert.Empty(read.Rows);
        Assert.Equal(2, read.Problems.Count);
    }

    [Fact]
    public void An_unknown_key_is_refused_rather_than_ignored()
    {
        // A typo in a field name is a field silently not applied. "slaDay: 45" would leave
        // the company with no target at all if the reader shrugged at it.
        var read = Read(
            ("some-company/broker.yaml", Row.Replace("slaDays: 45", "slaDay: 45", StringComparison.Ordinal)),
            ("some-company/email.yaml", Mailbox));

        Assert.Empty(read.Rows);
        Assert.NotEmpty(read.Problems);
    }

    [Fact]
    public void Every_problem_is_reported_rather_than_the_first_one()
    {
        var read = Read(
            ("some-company/broker.yaml", Row
                .Replace("slaDays: 45", "slaDays: 0", StringComparison.Ordinal)
                .Replace("removalMethod: email", "removalMethod: carrier-pigeon", StringComparison.Ordinal)
                .Replace("https://some-company.com/privacy", "nowhere", StringComparison.Ordinal)),
            ("some-company/email.yaml", Mailbox));

        Assert.Equal(3, read.Problems.Count);
    }
}
