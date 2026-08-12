// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using DbUp.Engine.Output;
using Dbr.Migrator;

// One-shot. Self-hosted, this is the compose service the API and Worker gate on with
// `service_completed_successfully`; hosted, the identical binary is an explicit
// pre-deploy step run exactly once (§18.5) — deliberately not something either
// application does at boot, since replicas racing to migrate is the failure mode the
// separate step exists to avoid.
var runner = new MigrationRunner(
    Assembly.GetExecutingAssembly(),
    Environment.GetEnvironmentVariable,
    new ConsoleUpgradeLog());

var exitCode = runner.Run(MigrationSet.All);

Console.Out.WriteLine(
    exitCode == MigrationRunner.ExitSuccess
        ? "Migrations up to date."
        : $"Migration run failed with exit code {exitCode}.");

// The exit code is the contract: compose's `service_completed_successfully` gate and
// a CI/CD pre-deploy step both read it, and both must refuse to proceed on failure.
return exitCode;
