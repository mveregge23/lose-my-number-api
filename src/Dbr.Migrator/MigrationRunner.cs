// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using DbUp;
using DbUp.Engine.Output;

namespace Dbr.Migrator;

/// <summary>
/// The only thing in this system that ever changes the database schema.
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
            // Not DbUp's default, and worth being explicit about: Postgres DDL is
            // transactional, and this is what turns that into the property migrations
            // are reviewed on the assumption of — a script failing halfway leaves the
            // schema exactly where it started rather than half-applied. Without it,
            // recovering from a partial migration is a manual job against production.
            // Note for later: CREATE INDEX CONCURRENTLY cannot run inside a
            // transaction, so the first migration wanting one needs a deliberate
            // exception here, not a quiet removal of this line.
            .WithTransactionPerScript()
            // DbUp substitutes $token$ placeholders by default and throws on one it
            // has no value for. plpgsql dollar-quoting uses exactly that shape — a
            // function body delimited $body$ ... $body$ would be read as a template
            // variable and fail the migration. Nothing here templates anything, so
            // the feature is turned off rather than worked around at each call site.
            .WithVariablesDisabled()
            .JournalToPostgresqlTable("public", set.JournalTable)
            .LogTo(log)
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            log.LogError(
                "Migration set '{0}' failed on {1}. The schema is unchanged — this script "
                + "ran in a transaction and rolled back. Migrations are forward-only: "
                + "correct the script if it has never succeeded anywhere, otherwise add a "
                + "new one that fixes what it did.",
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
