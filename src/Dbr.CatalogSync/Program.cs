// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.CatalogSync;

// One-shot, run after the migrator and before anything serves traffic — the same shape
// and for the same reason: several replicas applying the catalog at once would be racing
// over the same rows, and a deploy step runs exactly once.
//
// `--check` is the half that needs no database, which is what CI runs on a pull request.
// A malformed file should fail review rather than a deploy: by the time a bad file
// reaches a deploy, reviewers have already approved it.
var check = args.Contains("--check", StringComparer.Ordinal);

var catalog = CatalogReader.Read(Assembly.GetExecutingAssembly());
var brokers = BrokerReader.Read(Assembly.GetExecutingAssembly());

var problems = catalog.Problems.Concat(brokers.Problems).ToList();

foreach (var problem in problems)
{
    await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
}

if (problems.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"{problems.Count} problem(s) in the catalog. Nothing was applied.").ConfigureAwait(false);

    return 1;
}

Console.Out.WriteLine($"{catalog.Rows.Count} legal-basis rows read from the catalog.");
Console.Out.WriteLine($"{brokers.Rows.Count} companies read from the catalog.");

foreach (var example in brokers.WorkedExamples)
{
    // Validated with the rest so the example cannot drift from the schema, and never
    // applied so no instance ever paces a lane for a company that does not exist.
    Console.Out.WriteLine($"Worked example, not applied: {example}");
}

if (check)
{
    Console.Out.WriteLine("Checked only; no database was touched.");

    return 0;
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Core");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // Named the same way the migrator names it, because they run against the same
    // database as the same role and an operator should not have to learn a second
    // variable to configure one deploy.
    await Console.Error.WriteLineAsync(
        "ConnectionStrings__Core is not set. The catalog sync writes rows the application "
        + "role is not allowed to write, so it connects as the owner, exactly as the migrator "
        + "does.").ConfigureAwait(false);

    return 1;
}

try
{
    var result = await new CatalogSyncRunner(connectionString)
        .RunAsync(catalog.Rows, brokers.Rows)
        .ConfigureAwait(false);

    Console.Out.WriteLine(
        $"Catalog applied: {result.Applied} legal-basis row(s) written, {result.Retracted} retracted; "
        + $"{result.BrokersApplied} compan(ies) written, {result.BrokersRetracted} deactivated.");

    foreach (var claimed in result.LeftAlone)
    {
        // Not a failure. This instance has its own reading of a regime the shared catalog
        // also describes, and keeping it is the point — but somebody wondering why an
        // update never arrived deserves to find the answer in the log.
        Console.Out.WriteLine($"Left alone (this instance owns it): {claimed}");
    }

    return 0;
}
catch (CatalogSyncRefusedException blocked)
{
    await Console.Error.WriteLineAsync(blocked.Message).ConfigureAwait(false);

    return 1;
}
