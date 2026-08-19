// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.CatalogSync;

namespace Dbr.CatalogSync.Tests;

/// <summary>
/// What the reader refuses.
/// </summary>
/// <remarks>
/// <para>
/// Everything interesting here is a refusal, so these hand it files that are wrong. The
/// cases chosen are the ones that read perfectly well in a pull request: a citation that
/// is not a link, a region spelled the way a person would say it, a missing unit. None of
/// them look like mistakes in a diff, and each becomes a wrong answer given to somebody
/// months later.
/// </para>
/// <para>
/// A file with anything wrong contributes no rows at all, rather than its good entries.
/// Half a reviewed reading is not a reviewed reading.
/// </para>
/// </remarks>
public class CatalogValidationTests
{
    private const string Good = """
        code: TESTCODE
        residencyScope: US-CA
        reviewedAt: 2026-08-18
        reviewedBy: "@somebody"
        requests:
          - requestType: delete
            responseDeadlineDays: 45
            extensionDays: 45
            deadlineUnit: calendar
            verificationLevel: basic
            citationUrl: https://example.test/statute
        """;

    private static CatalogReadResult Read(string yaml) =>
        CatalogReader.Read([("test.yaml", yaml)]);

    [Fact]
    public void A_well_formed_file_produces_its_row()
    {
        var catalog = Read(Good);

        Assert.Empty(catalog.Problems);
        var row = Assert.Single(catalog.Rows);
        Assert.Equal("TESTCODE", row.Code);
    }

    [Fact]
    public void A_citation_that_is_not_a_link_is_refused()
    {
        // "see the statute" is the shape of a citation somebody types when they mean to
        // come back to it, and it makes the row uncheckable.
        var catalog = Read(Good.Replace(
            "https://example.test/statute", "see the CCPA", StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }

    [Fact]
    public void An_http_citation_is_refused_along_with_the_rest()
    {
        var catalog = Read(Good.Replace(
            "https://example.test/statute", "http://example.test/statute", StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
    }

    [Fact]
    public void A_region_written_the_way_somebody_says_it_is_refused()
    {
        // 'California' matches no profile's region, so the regime would protect nobody
        // and nothing would look broken.
        var catalog = Read(Good.Replace("US-CA", "California", StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }

    [Fact]
    public void A_missing_deadline_unit_is_refused_rather_than_assumed_to_be_calendar()
    {
        // Assuming calendar was wrong once already, for the one rule in the catalog
        // counted in business days.
        var catalog = Read(Good.Replace(
            "    deadlineUnit: calendar\n", string.Empty, StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }

    [Fact]
    public void A_missing_extension_is_refused_rather_than_read_as_none()
    {
        // Zero states that the regime grants no extension. Absent states that nobody
        // filled it in, and the two should not collapse into each other.
        var catalog = Read(Good.Replace(
            "    extensionDays: 45\n", string.Empty, StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
    }

    [Fact]
    public void A_file_with_no_reviewer_is_refused()
    {
        var catalog = Read(Good.Replace(
            "reviewedBy: \"@somebody\"\n", string.Empty, StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }

    [Fact]
    public void A_deadline_of_zero_is_refused()
    {
        var catalog = Read(Good.Replace(
            "responseDeadlineDays: 45", "responseDeadlineDays: 0", StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
    }

    [Fact]
    public void An_unknown_request_type_is_refused()
    {
        var catalog = Read(Good.Replace(
            "requestType: delete", "requestType: forget_me", StringComparison.Ordinal));

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }

    [Fact]
    public void One_bad_entry_takes_the_whole_file_with_it()
    {
        // Two requests, one of them missing its citation. Applying the good one would
        // put half a reviewed reading into the catalog under a code that claims to have
        // been reviewed whole.
        var twoRequests = Good + """

              - requestType: opt_out_sale
                responseDeadlineDays: 15
                extensionDays: 0
                deadlineUnit: business
                verificationLevel: none
            """;

        var catalog = Read(twoRequests);

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }

    [Fact]
    public void Two_files_describing_one_regime_are_refused()
    {
        // Which reading wins would come down to filename order.
        var catalog = CatalogReader.Read([("a.yaml", Good), ("b.yaml", Good)]);

        Assert.NotEmpty(catalog.Problems);
    }

    [Fact]
    public void Every_problem_is_reported_rather_than_the_first_one()
    {
        // Somebody fixing a file wants the whole list. A validator stopping at the first
        // error turns one review into four.
        var broken = Good
            .Replace("https://example.test/statute", "nope", StringComparison.Ordinal)
            .Replace("US-CA", "California", StringComparison.Ordinal);

        Assert.True(Read(broken).Problems.Count > 1);
    }

    [Fact]
    public void A_file_that_is_not_yaml_at_all_is_reported_rather_than_thrown()
    {
        var catalog = Read("code: [unclosed\n  - broken: :");

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }
}
