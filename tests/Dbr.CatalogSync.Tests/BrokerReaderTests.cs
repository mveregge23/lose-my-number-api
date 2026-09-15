// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.CatalogSync;

namespace Dbr.CatalogSync.Tests;

/// <summary>
/// The company files as the sync reads them.
/// </summary>
/// <remarks>
/// Against the files compiled into the sync assembly, for the same reason the regime
/// tests are: a glob that stopped matching would otherwise show up as every company
/// deactivated on the next deploy rather than as a failure here.
/// </remarks>
public class BrokerReaderTests
{
    private static readonly Assembly Sync = typeof(BrokerRow).Assembly;

    private static BrokerReadResult Read() => BrokerReader.Read(Sync);

    [Fact]
    public void The_shipped_catalog_reads_without_complaint()
    {
        Assert.Empty(Read().Problems);
    }

    [Fact]
    public void The_worked_example_is_read_and_never_applied()
    {
        // It has to be read, because it is the template a contributor copies and a
        // template that has drifted from the schema teaches the wrong shape. It must not
        // be applied, because every instance would then pace a lane for a company that
        // does not exist and offer to send demands to it.
        var read = Read();

        var example = Assert.Single(read.WorkedExamples);
        Assert.Contains("example-broker.example", example, StringComparison.Ordinal);
        Assert.DoesNotContain(read.Rows, row => BrokerReader.IsReserved(row.Domain));
    }

    [Fact]
    public void The_worked_example_and_its_recipes_agree_about_which_company_they_are()
    {
        // Three files, one id, checked by the reader rather than by whoever last edited
        // one of them. If this ever fails the shipped recipes are for a company the
        // shipped row does not describe.
        Assert.DoesNotContain(
            Read().Problems,
            problem => problem.Contains("example-broker", StringComparison.Ordinal));
    }

    [Fact]
    public void No_real_company_ships_yet()
    {
        // Written to be deleted. The catalog has no companies in it, KNOWN-GAPS.md says
        // so, and the day the first real one lands this fails and both get updated —
        // which is the point: a company arriving should be noticed, not slipped in.
        Assert.Empty(Read().Rows);
    }

    [Fact]
    public void A_build_carrying_no_company_files_at_all_is_a_problem_rather_than_an_empty_catalog()
    {
        // Zero companies is honest — it is the state this catalog is in. Zero files is
        // not, because the worked example always ships, so a build with none has a glob
        // that stopped matching. Applying it would deactivate every company on every
        // instance while reporting a clean, empty catalog.
        var read = BrokerReader.Read([]);

        var problem = Assert.Single(read.Problems);
        Assert.Contains("glob", problem, StringComparison.Ordinal);
    }
}
