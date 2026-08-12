// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Text.RegularExpressions;
using Dbr.Migrator;

namespace Dbr.Migrator.Tests;

/// <summary>
/// The migration scripts themselves, as the migrator sees them.
/// </summary>
/// <remarks>
/// These run against the compiled resources rather than the files on disk, which is
/// the point: a broken wildcard in the csproj would leave a script on disk, in code
/// review, and out of every database. <see cref="Both_sets_are_wired_up"/> is what
/// makes the rest of these non-vacuous.
/// </remarks>
public partial class MigrationScriptTests
{
    private static readonly Assembly Migrator = typeof(MigrationSet).Assembly;

    /// <summary><c>&lt;set&gt;.YYYYMMDD_HHMM__short_description.sql</c>, per §18.4.</summary>
    [GeneratedRegex(@"^(core|vault)\.[0-9]{8}_[0-9]{4}__[a-z0-9_]+\.sql$")]
    private static partial Regex ScriptName();

    private static IReadOnlyList<string> AllScripts() =>
        [.. Migrator.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal))];

    [Fact]
    public void Both_sets_are_wired_up()
    {
        // Not a tautology check: if the csproj globs ever stop matching, every other
        // test here would pass over an empty list and report the schema healthy.
        Assert.NotEmpty(AllScripts());
        Assert.NotEmpty(MigrationSet.Vault.ScriptNames(Migrator));
    }

    [Fact]
    public void Every_script_follows_the_naming_convention()
    {
        var offenders = AllScripts().Where(name => !ScriptName().IsMatch(name)).ToList();

        Assert.True(
            offenders.Count == 0,
            "Migration filenames must be YYYYMMDD_HHMM__short_description.sql, lower-case, "
            + "under db/migrations/core or db/migrations/vault. The timestamp is what makes "
            + "filename order the order scripts run in. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_script_belongs_to_exactly_one_set()
    {
        foreach (var script in AllScripts())
        {
            var owners = MigrationSet.All.Where(set => set.Owns(script)).ToList();

            Assert.True(
                owners.Count == 1,
                $"'{script}' is claimed by {owners.Count} sets ({string.Join(", ", owners.Select(o => o.Name))}). "
                + "A script applied by two runners against two journals is applied twice.");
        }
    }

    [Fact]
    public void Scripts_run_in_chronological_order()
    {
        foreach (var set in MigrationSet.All)
        {
            var names = set.ScriptNames(Migrator);

            // The timestamp prefix is only useful if name order really is time order,
            // which holds because the format is fixed-width and zero-padded.
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
        }
    }

    [Fact]
    public void The_two_sets_journal_separately()
    {
        // §18.4's whole reason for two sets: promoting the vault to its own database
        // must stay a connection-string change, which it isn't if they share a journal.
        Assert.NotEqual(MigrationSet.Core.JournalTable, MigrationSet.Vault.JournalTable);
        Assert.NotEqual(
            MigrationSet.Core.ConnectionStringVariable,
            MigrationSet.Vault.ConnectionStringVariable);
    }

    [Fact]
    public void Core_is_applied_before_vault()
    {
        Assert.Equal(
            [MigrationSet.Core, MigrationSet.Vault],
            MigrationSet.All);
    }
}
