// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.Domain.Catalog;
using Dbr.Domain.Regions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dbr.CatalogSync;

/// <summary>One row, as the files describe it.</summary>
public sealed record CatalogRow(
    string Code,
    LegalRequestType RequestType,
    string ResidencyScope,
    int ResponseDeadlineDays,
    int ExtensionDays,
    DeadlineUnit DeadlineUnit,
    VerificationLevel VerificationLevel,
    string CitationUrl,
    DateTimeOffset ReviewedAt,
    string ReviewedBy);

/// <param name="Rows">Every row the files describe, when nothing is wrong with them.</param>
/// <param name="Problems">
/// Everything wrong, rather than the first thing wrong. Somebody fixing a file wants the
/// whole list — a validator that stops at the first error turns one review into four.
/// </param>
public sealed record CatalogReadResult(IReadOnlyList<CatalogRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Reads the curated legal-basis files and says whether they are usable.
/// </summary>
/// <remarks>
/// <para>
/// This is the half that runs without a database, which is what makes it the check CI
/// can perform on a pull request. A malformed file should fail review, not a deploy —
/// by the time a bad file reaches a deploy the reviewers have already approved it.
/// </para>
/// <para>
/// Validation is deliberately strict about the things a reviewer cannot see. A missing
/// citation, a region spelled in a way nothing will match, a deadline of zero: each of
/// them reads fine in a diff and is wrong in a way that only shows up as a wrong answer
/// given to somebody months later.
/// </para>
/// </remarks>
public static class CatalogReader
{
    private const string ResourcePrefix = "legal-basis.";

    /// <summary>Reads the catalog compiled into an assembly.</summary>
    public static CatalogReadResult Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var files = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);

                return (Name: name[ResourcePrefix.Length..], Yaml: reader.ReadToEnd());
            });

        return Read(files);
    }

    /// <summary>
    /// Reads a catalog from files already in hand.
    /// </summary>
    /// <remarks>
    /// Separate from the assembly overload so the validation can be exercised against
    /// files that are wrong. Everything interesting this does is refusal, and a reader
    /// that could only be handed the shipped catalog could only ever be tested on content
    /// already known to be good.
    /// </remarks>
    public static CatalogReadResult Read(IEnumerable<(string Name, string Yaml)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)

            // An unknown key is a typo, and a typo in this content is a field silently
            // not applied — a citation that never reaches the row, an extension nobody
            // notices is missing. Refusing is what turns that into a failed check.
            .WithDuplicateKeyChecking()
            .Build();

        var rows = new List<CatalogRow>();
        var problems = new List<string>();

        var named = files.ToList();

        if (named.Count == 0)
        {
            problems.Add(
                "No legal-basis files are compiled into this build. Either the catalog is empty or "
                + "the csproj glob stopped matching, and the second one looks exactly like the first "
                + "from here.");
        }

        foreach (var (name, yaml) in named)
        {
            ReadFile(deserializer, name, yaml, rows, problems);
        }

        foreach (var duplicate in rows
            .GroupBy(row => (row.Code, row.RequestType, row.ResidencyScope))
            .Where(group => group.Count() > 1))
        {
            // The same key twice across two files is two readings of one regime, and
            // which one wins would come down to filename order.
            problems.Add(
                $"{duplicate.Key.Code} / {CatalogVocabulary.ToWire(duplicate.Key.RequestType)} / "
                + $"{duplicate.Key.ResidencyScope} is described by more than one file.");
        }

        return new CatalogReadResult(rows, problems);
    }

    private static void ReadFile(
        IDeserializer deserializer,
        string file,
        string yaml,
        List<CatalogRow> rows,
        List<string> problems)
    {
        LegalBasisFile? parsed;

        try
        {
            parsed = deserializer.Deserialize<LegalBasisFile>(yaml);
        }
        catch (YamlDotNet.Core.YamlException failure)
        {
            problems.Add($"{file}: not readable as YAML — {failure.Message}");

            return;
        }

        if (parsed is null)
        {
            problems.Add($"{file}: empty.");

            return;
        }

        var before = problems.Count;

        if (string.IsNullOrWhiteSpace(parsed.Code))
        {
            problems.Add($"{file}: no code. A regime has to be citable by the name it is cited under.");
        }

        var scope = RegionCode.Normalize(parsed.ResidencyScope);

        if (!RegionCode.IsWellFormed(scope))
        {
            problems.Add(
                $"{file}: residencyScope '{parsed.ResidencyScope}' is not a region code such as "
                + "'US-CA'. A scope spelled any other way protects nobody, because it is matched "
                + "against a profile's region exactly.");
        }

        if (parsed.ReviewedAt is null)
        {
            problems.Add($"{file}: no reviewedAt.");
        }

        if (string.IsNullOrWhiteSpace(parsed.ReviewedBy))
        {
            problems.Add(
                $"{file}: no reviewedBy. A row that cannot say who stands behind the reading is "
                + "worse than an absent one, which at least falls back to a courtesy target.");
        }

        if (parsed.Requests.Count == 0)
        {
            problems.Add($"{file}: grants nothing. A regime with no requests is a row nothing can use.");
        }

        foreach (var entry in parsed.Requests)
        {
            ReadEntry(file, parsed, scope, entry, rows, problems);
        }

        if (problems.Count != before)
        {
            // Anything already added for this file means its rows are not trustworthy;
            // keeping the good entries would apply half a reviewed reading.
            rows.RemoveAll(row => row.Code == parsed.Code?.Trim());
        }
    }

    private static void ReadEntry(
        string file,
        LegalBasisFile parsed,
        string? scope,
        LegalBasisRequestEntry entry,
        List<CatalogRow> rows,
        List<string> problems)
    {
        var requestType = CatalogVocabulary.ParseLegalRequestType(entry.RequestType?.Trim());
        var unit = CatalogVocabulary.ParseDeadlineUnit(entry.DeadlineUnit?.Trim());
        var verification = CatalogVocabulary.ParseVerificationLevel(entry.VerificationLevel?.Trim());

        var where = $"{file} / {entry.RequestType ?? "(no requestType)"}";

        if (requestType is null)
        {
            problems.Add(
                $"{where}: requestType is 'delete', 'opt_out_sale' or 'opt_out_targeted_ads'.");
        }

        if (unit is null)
        {
            problems.Add(
                $"{where}: deadlineUnit is 'calendar' or 'business'. It is required rather than "
                + "defaulted, because assuming calendar was wrong once already.");
        }

        if (verification is null)
        {
            problems.Add($"{where}: verificationLevel is 'none', 'basic' or 'enhanced'.");
        }

        if (entry.ResponseDeadlineDays is not > 0)
        {
            problems.Add(
                $"{where}: responseDeadlineDays must be a real window. Zero would be a deadline "
                + "already past when the request is made.");
        }

        if (entry.ExtensionDays is not >= 0)
        {
            problems.Add(
                $"{where}: extensionDays is required and may be zero, which states that the regime "
                + "grants no extension — a different thing from nobody having filled it in.");
        }

        if (!IsCitation(entry.CitationUrl))
        {
            problems.Add(
                $"{where}: citationUrl must be an https link to the source this was read from.");
        }

        if (requestType is null || unit is null || verification is null || scope is null
            || entry.ResponseDeadlineDays is not > 0 || entry.ExtensionDays is not >= 0
            || string.IsNullOrWhiteSpace(parsed.Code) || parsed.ReviewedAt is null
            || string.IsNullOrWhiteSpace(parsed.ReviewedBy) || !IsCitation(entry.CitationUrl))
        {
            return;
        }

        rows.Add(new CatalogRow(
            parsed.Code.Trim(),
            requestType.Value,
            scope,
            entry.ResponseDeadlineDays.Value,
            entry.ExtensionDays.Value,
            unit.Value,
            verification.Value,
            entry.CitationUrl!.Trim(),

            // Midnight UTC on the day named, whatever zone this happens to run in. See
            // the note on the field: a review date read as a local instant makes one file
            // mean several different things.
            new DateTimeOffset(DateTime.SpecifyKind(parsed.ReviewedAt.Value.Date, DateTimeKind.Utc)),
            parsed.ReviewedBy!.Trim()));
    }

    private static bool IsCitation(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps;
}
