// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using DbUp.Engine.Output;
using Dbr.Migrator;

namespace Dbr.Migrator.Tests;

/// <summary>
/// The runner's behaviour before it reaches a database. Applying scripts for real is
/// exercised by <c>docker compose up</c> today and by the Testcontainers harness in
/// DBR-085; what matters here is that a misconfigured run fails without touching
/// anything.
/// </summary>
public class MigrationRunnerTests
{
    private static readonly Assembly Migrator = typeof(MigrationSet).Assembly;

    [Fact]
    public void A_missing_connection_string_stops_the_run_before_any_connection()
    {
        var log = new RecordingLog();
        var runner = new MigrationRunner(Migrator, _ => null, log);

        var exitCode = runner.Run(MigrationSet.All);

        Assert.Equal(MigrationRunner.ExitConfigurationError, exitCode);
        // Naming the variable is the whole value of failing here — the person reading
        // this line in a compose log needs to know what to go and set.
        Assert.Contains(MigrationSet.Core.ConnectionStringVariable, log.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_connection_string_counts_as_missing(string value)
    {
        var runner = new MigrationRunner(Migrator, _ => value, new RecordingLog());

        Assert.Equal(MigrationRunner.ExitConfigurationError, runner.Run(MigrationSet.All));
    }

    [Fact]
    public void Every_connection_string_is_resolved_before_the_first_one_is_used()
    {
        // Core configured, vault not. Migrating core and only then discovering the
        // vault variable is unset would leave the two stores at different versions —
        // worse than refusing to start, because it looks like a successful partial run.
        var log = new RecordingLog();
        var runner = new MigrationRunner(
            Migrator,
            name => name == MigrationSet.Core.ConnectionStringVariable
                ? "Host=example.invalid;Database=dbr;Username=dbr;Password=x"
                : null,
            log);

        var exitCode = runner.Run(MigrationSet.All);

        Assert.Equal(MigrationRunner.ExitConfigurationError, exitCode);
        Assert.Contains(MigrationSet.Vault.ConnectionStringVariable, log.Errors);
        // Nothing was attempted: had core been applied first, the unreachable host
        // would have produced a migration failure instead of a configuration one.
        Assert.DoesNotContain("applied", log.Everything, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingLog : IUpgradeLog
    {
        private readonly List<string> _errors = [];
        private readonly List<string> _all = [];

        public string Errors => string.Join('\n', _errors);

        public string Everything => string.Join('\n', _all);

        public void LogTrace(string format, params object[] args) => Record(_all, format, args);

        public void LogDebug(string format, params object[] args) => Record(_all, format, args);

        public void LogInformation(string format, params object[] args) => Record(_all, format, args);

        public void LogWarning(string format, params object[] args) => Record(_all, format, args);

        public void LogError(string format, params object[] args)
        {
            Record(_all, format, args);
            Record(_errors, format, args);
        }

        public void LogError(Exception ex, string format, params object[] args)
        {
            Record(_all, format, args);
            Record(_errors, format, args);
        }

        private static void Record(List<string> sink, string format, object[] args) =>
            sink.Add(args.Length == 0 ? format : string.Format(format, args));
    }
}
