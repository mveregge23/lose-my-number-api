// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.CatalogSync;
using Dbr.Domain.Catalog;
using Dbr.Domain.Regions;

namespace Dbr.CatalogSync.Tests;

/// <summary>
/// The catalog files as the sync reads them.
/// </summary>
/// <remarks>
/// These run against the files compiled into the sync assembly, which is the same thing
/// a deploy would apply. A test reading the directory off disk would pass for a build
/// whose glob had stopped matching, and the resulting deploy would apply an empty
/// catalog and retract every row in it.
/// </remarks>
public class CatalogReaderTests
{
    private static readonly Assembly Sync = typeof(CatalogRow).Assembly;

    private static CatalogReadResult Read() => CatalogReader.Read(Sync);

    [Fact]
    public void The_shipped_catalog_reads_without_complaint()
    {
        var catalog = Read();

        Assert.Empty(catalog.Problems);
        Assert.NotEmpty(catalog.Rows);
    }

    [Fact]
    public void Every_jurisdiction_the_seed_carried_is_in_the_files()
    {
        // The content moved out of a migration and into files; these are the five it
        // moved. A file quietly not compiled in would otherwise show up as a retraction
        // on the next deploy rather than as a failure here.
        var codes = Read().Rows.Select(row => row.Code).Distinct().ToList();

        foreach (var code in new[] { "CCPA", "VCDPA", "CPA", "CTDPA", "UCPA" })
        {
            Assert.Contains(code, codes);
        }
    }

    [Fact]
    public void Every_jurisdiction_grants_all_three_request_types()
    {
        // Fifteen rows across five files. A regime seeded with only its opt-outs would
        // quietly resolve every deletion to the broker's courtesy target while looking
        // covered.
        var rows = Read().Rows;

        Assert.Equal(15, rows.Count);

        foreach (var jurisdiction in rows.GroupBy(row => row.Code))
        {
            Assert.Equal(3, jurisdiction.Select(row => row.RequestType).Distinct().Count());
        }
    }

    [Fact]
    public void The_one_regime_counted_in_business_days_still_says_so()
    {
        // The reading that took the most getting right, and the one a careless edit to a
        // file would flatten back to calendar days.
        var californian = Read().Rows
            .Where(row => row.Code == "CCPA" && row.RequestType != LegalRequestType.Delete)
            .ToList();

        Assert.NotEmpty(californian);

        foreach (var row in californian)
        {
            Assert.Equal(DeadlineUnit.Business, row.DeadlineUnit);
            Assert.Equal(15, row.ResponseDeadlineDays);
        }
    }

    [Fact]
    public void Every_row_carries_provenance_that_can_be_followed()
    {
        foreach (var row in Read().Rows)
        {
            Assert.StartsWith("https://", row.CitationUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(row.ReviewedBy));
            Assert.True(RegionCode.IsWellFormed(row.ResidencyScope));
        }
    }

    [Fact]
    public void A_build_carrying_no_catalog_at_all_is_a_problem_rather_than_an_empty_one()
    {
        // The failure mode that matters most for a sync that retracts: a csproj glob
        // that stops matching produces a build with nothing in it, and applying that
        // would delete every catalog row rather than change none.
        var catalog = CatalogReader.Read(typeof(CatalogReaderTests).Assembly);

        Assert.NotEmpty(catalog.Problems);
        Assert.Empty(catalog.Rows);
    }
}
