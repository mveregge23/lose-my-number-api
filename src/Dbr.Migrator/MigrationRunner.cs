// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using DbUp;
using DbUp.Engine.Output;

namespace Dbr.Migrator;

/// <summary>
/// Applies the migration sets of §18.4. The only thing in this system that ever
/// changes the schema.
/// </summary>
public sealed class MigrationRunner(
    Assembly scriptAssembly,
    Func<string, string?> readEnvironmentVariable,
    IUpgradeLog log)
{
    /// <summary>Every set applied cleanly, or there was nothing to apply.</summary>
    public const int ExitSuccess = 0;

    /// <summary>A script failed. Postgres rolled it back; the schema is unchanged.</summary>
    public const int ExitMigrationFailed = 1;

    /// <summary>A connection string is missing — nothing was attempted.</summary>
    public const int ExitConfigurationError = 2;

    public int Run(IReadOnlyList<MigrationSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        // Resolve every connection string before touching the database. A run that
        // migrates core and then discovers the vault variable is unset would leave
        // the two stores at different versions, which is worse than not starting.
        var resolved = new List<(MigrationSet Set, string ConnectionString)>(sets.Count);

        foreach (var set in sets)
        {
            var connectionString = readEnvironmentVariable(set.ConnectionStringVariable);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                log.LogError(
                    "No connection string for the '{0}' migration set: {1} is unset or blank. "
                    + "docker-compose.yml sets it for the migrator service; a pre-deploy run "
                    + "supplies it from the deployment environment.",
                    set.Name,
                    set.ConnectionStringVariable);

                return ExitConfigurationError;
            }

            resolved.Add((set, connectionString));
        }

        foreach (var (set, connectionString) in resolved)
        {
            if (!Apply(set, connectionString))
            {
                return ExitMigrationFailed;
            }
        }

        return ExitSuccess;
    }

    private bool Apply(MigrationSet set, string connectionString)
    {
        var scriptNames = set.ScriptNames(scriptAssembly);

        log.LogInformation(
            "Migration set '{0}': {1} script(s) available, journal {2}.",
            set.Name,
            scriptNames.Count,
            set.JournalTable);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(scriptAssembly, set.Owns)
            // §18.3: Postgres DDL is transactional, and this is what turns that fact
            // into the safety property the forward-only decision leans on — a script
            // that fails midway leaves the schema exactly where it started rather
            // than half-applied. Note for later: a CREATE INDEX CONCURRENTLY cannot
            // run inside a transaction, so the first migration that wants one needs
            // a deliberate exception here, not a quiet removal of this line.
            .WithTransactionPerScript()
            .JournalToPostgresqlTable("public", set.JournalTable)
            .LogTo(log)
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            log.LogError(
                "Migration set '{0}' failed on {1}. The schema is unchanged — this script "
                + "ran in a transaction and rolled back. Fix forward (§18.3): correct the "
                + "script if it has never succeeded anywhere, otherwise add a new one.",
                set.Name,
                result.ErrorScript?.Name ?? "an unnamed script");

            return false;
        }

        log.LogInformation(
            "Migration set '{0}': applied {1} script(s).",
            set.Name,
            result.Scripts.Count());

        return true;
    }
}
