// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.Domain.Catalog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dbr.CatalogSync;

/// <summary>One company, as its file describes it.</summary>
public sealed record BrokerRow(
    Guid Id,
    string Name,
    string Domain,
    RemovalMethod RemovalMethod,
    int SlaDays,
    EmailContactMode EmailContactMode,
    bool Active,
    int MaxConcurrency,
    int MinDelayMs,
    int RateLimitThreshold,
    int CooldownMinutes,
    int FormChangeThreshold);

/// <param name="Rows">Every company the files describe and the sync should apply.</param>
/// <param name="WorkedExamples">
/// Companies described under a reserved domain, which are validated like any other and
/// never applied. See <see cref="BrokerReader.IsReserved"/>.
/// </param>
/// <param name="Problems">Everything wrong, rather than the first thing wrong.</param>
public sealed record BrokerReadResult(
    IReadOnlyList<BrokerRow> Rows,
    IReadOnlyList<string> WorkedExamples,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads the curated company files and says whether they are usable.
/// </summary>
/// <remarks>
/// <para>
/// The same job <see cref="CatalogReader"/> does for regimes, with one thing the regimes
/// never needed: a company is three documents rather than one. The row is here, how to
/// search the company is <c>search.yaml</c>, and where it takes a demand is
/// <c>email.yaml</c>, and each recipe names its company by id. The engines that run the
/// recipes validate their contents; what only this reader can see is whether the id they
/// name is the company they sit beside. A recipe bound to a company nobody has described
/// is one that never runs, and nothing else would ever say so.
/// </para>
/// <para>
/// A company under a reserved domain is a worked example. It is read and checked exactly
/// like a real one, so the example cannot drift from the schema, and it is reported rather
/// than applied, so no instance ever paces a lane for a company that does not exist or
/// sends a demand to <c>example.com</c>. The rule is the domain rather than a flag in the
/// file, because a flag is a thing somebody forgets to set.
/// </para>
/// </remarks>
public static class BrokerReader
{
    private const string ResourcePrefix = "brokers.";

    private const string RowFile = "broker.yaml";
    private const string SearchFile = "search.yaml";
    private const string MailboxFile = "email.yaml";

    /// <summary>Reads the company files compiled into an assembly.</summary>
    public static BrokerReadResult Read(Assembly assembly)
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
    /// Reads companies from files already in hand, named by their path under
    /// <c>catalog/brokers/</c> — <c>some-company/broker.yaml</c>.
    /// </summary>
    public static BrokerReadResult Read(IEnumerable<(string Name, string Yaml)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var strict = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        // The recipes are somebody else's documents with their own keys; this only wants
        // the one line naming the company, and refusing the rest would make this reader a
        // second, worse validator of files that already have a real one.
        var header = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var rows = new List<BrokerRow>();
        var examples = new List<string>();
        var problems = new List<string>();

        var byDirectory = files
            .Select(file => (Path: file.Name.Replace('\\', '/'), file.Yaml))
            .GroupBy(file => DirectoryOf(file.Path), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        if (byDirectory.Count == 0)
        {
            // Zero companies is a state this catalog can honestly be in; zero files is
            // not, because the worked example always ships. A build with none has a glob
            // that stopped matching, and applying it would deactivate every company on
            // every instance while looking exactly like an empty catalog.
            problems.Add(
                "No company files are compiled into this build — not even the worked example. "
                + "The csproj glob has stopped matching, and applying this would deactivate "
                + "every catalog company on every instance.");
        }

        foreach (var directory in byDirectory)
        {
            ReadDirectory(strict, header, directory.Key, directory.ToList(), rows, examples, problems);
        }

        var applied = rows.Where(row => !IsReserved(row.Domain)).ToList();

        foreach (var duplicate in rows.GroupBy(row => row.Id).Where(group => group.Count() > 1))
        {
            problems.Add(
                $"{duplicate.Key} is the id of more than one company. Recipes bind to the id, so "
                + "two companies sharing one would be searched and sent to as each other.");
        }

        foreach (var duplicate in rows.GroupBy(row => row.Domain, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add(
                $"{duplicate.Key} is the domain of more than one company. One company under two "
                + "rows would be paced as two lanes and could be sent the same person twice.");
        }

        foreach (var row in rows.Where(row => IsReserved(row.Domain)))
        {
            examples.Add($"{row.Domain} ({row.Name})");
        }

        return new BrokerReadResult(applied, examples, problems);
    }

    /// <summary>
    /// Whether a domain is one the internet reserves for documentation and testing, and
    /// therefore describes a worked example rather than a company.
    /// </summary>
    /// <remarks>
    /// The reserved top-level labels and the reserved second-level names of RFC 2606 and
    /// RFC 6761. Nothing under them resolves to a company, which is what makes the rule
    /// safe to apply without asking: a real company cannot be misread as an example, and
    /// an example cannot be written so that it looks real.
    /// </remarks>
    public static bool IsReserved(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var host = domain.Trim().ToLowerInvariant();

        return host is "example.com" or "example.net" or "example.org"
            || host.EndsWith(".example.com", StringComparison.Ordinal)
            || host.EndsWith(".example.net", StringComparison.Ordinal)
            || host.EndsWith(".example.org", StringComparison.Ordinal)
            || host.EndsWith(".test", StringComparison.Ordinal)
            || host.EndsWith(".example", StringComparison.Ordinal)
            || host.EndsWith(".invalid", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host == "localhost";
    }

    private static void ReadDirectory(
        IDeserializer strict,
        IDeserializer header,
        string directory,
        List<(string Path, string Yaml)> files,
        List<BrokerRow> rows,
        List<string> examples,
        List<string> problems)
    {
        var rowFile = files.FirstOrDefault(file => FileOf(file.Path) == RowFile);
        var recipes = files.Where(file => FileOf(file.Path) is SearchFile or MailboxFile).ToList();

        if (rowFile.Yaml is null)
        {
            // A directory of recipes and no company. Each recipe names an id, and without
            // this file that id is a row nothing will ever insert.
            problems.Add(
                $"{directory}/: has {string.Join(" and ", recipes.Select(file => FileOf(file.Path)))} "
                + $"and no {RowFile}. A recipe binds to a company by id, and this directory "
                + "describes no company for it to bind to.");

            return;
        }

        var row = ReadRow(strict, rowFile.Path, rowFile.Yaml, problems);

        var before = problems.Count;

        foreach (var recipe in recipes)
        {
            ReadRecipe(header, recipe.Path, recipe.Yaml, row, problems);
        }

        if (row is null || problems.Count != before)
        {
            // The directory is the unit of review. A row whose recipes are for somebody
            // else is not a company anyone has finished describing, and applying it would
            // be applying half of one.
            return;
        }

        if (row.RemovalMethod == RemovalMethod.Email
            && recipes.All(file => FileOf(file.Path) != MailboxFile))
        {
            // Not an inconsistency a reviewer would see: the row is fine and the directory
            // is fine. It is a demand that is accepted and then stays queued, because the
            // connector that would carry it has no mailbox to carry it to.
            problems.Add(
                $"{rowFile.Path}: takes demands by mail and no {MailboxFile} beside it says at "
                + "which mailbox. A demand to this company would be accepted and never sent.");

            return;
        }

        rows.Add(row);
    }

    private static BrokerRow? ReadRow(
        IDeserializer deserializer,
        string file,
        string yaml,
        List<string> problems)
    {
        BrokerFile? parsed;

        try
        {
            parsed = deserializer.Deserialize<BrokerFile>(yaml);
        }
        catch (YamlDotNet.Core.YamlException failure)
        {
            problems.Add($"{file}: not readable as YAML — {failure.Message}");

            return null;
        }

        if (parsed is null)
        {
            problems.Add($"{file}: empty.");

            return null;
        }

        var before = problems.Count;

        if (parsed.Id is null || parsed.Id == Guid.Empty)
        {
            problems.Add(
                $"{file}: no id. The id is what the recipes beside this file bind to, and what "
                + "every scan and demand will name; it is assigned here, once, and never changes.");
        }

        if (string.IsNullOrWhiteSpace(parsed.Name))
        {
            problems.Add($"{file}: no name.");
        }

        var domain = parsed.Domain?.Trim().ToLowerInvariant();

        if (!IsBareHost(domain))
        {
            problems.Add(
                $"{file}: domain '{parsed.Domain}' is not a bare host such as 'example.com'. "
                + "It is completed into a URL by the search and into an address by the mailbox, "
                + "so a scheme, a path or an at-sign here would be written into both.");
        }

        var method = CatalogVocabulary.ParseRemovalMethod(parsed.RemovalMethod?.Trim());

        if (method is null)
        {
            problems.Add($"{file}: removalMethod is 'webform', 'email', 'api' or 'postal'.");
        }

        if (parsed.SlaDays is not > 0)
        {
            problems.Add(
                $"{file}: slaDays must be a real window. It is what a request gets when no "
                + "statute applies, and zero would be a deadline already past.");
        }

        var contact = parsed.EmailContactMode is null
            ? EmailContactMode.AliasPreferred
            : CatalogVocabulary.ParseEmailContactMode(parsed.EmailContactMode.Trim());

        if (contact is null)
        {
            problems.Add($"{file}: emailContactMode is 'alias_preferred' or 'tenant_real_required'.");
        }

        var pacing = parsed.Pacing ?? new BrokerPacing();

        if (pacing.MaxConcurrency is not (null or > 0))
        {
            problems.Add($"{file}: pacing.maxConcurrency must be at least 1.");
        }

        if (pacing.MinDelayMs is < 0)
        {
            problems.Add($"{file}: pacing.minDelayMs cannot be negative.");
        }

        if (pacing.RateLimitThreshold is not (null or > 0))
        {
            problems.Add($"{file}: pacing.rateLimitThreshold must be at least 1.");
        }

        if (pacing.CooldownMinutes is not (null or > 0))
        {
            problems.Add($"{file}: pacing.cooldownMinutes must be at least 1.");
        }

        if (pacing.FormChangeThreshold is not (null or > 0))
        {
            problems.Add($"{file}: pacing.formChangeThreshold must be at least 1.");
        }

        if (!IsCitation(parsed.SourceUrl))
        {
            problems.Add(
                $"{file}: sourceUrl must be an https link to where the domain and method were "
                + "read — the company's privacy page, or a registry entry.");
        }

        if (problems.Count != before)
        {
            return null;
        }

        // The defaults are the table's own, restated here rather than left to the insert,
        // because the update path has to write something and "whatever the column defaults
        // to" is not a value an UPDATE can name.
        return new BrokerRow(
            parsed.Id!.Value,
            parsed.Name!.Trim(),
            domain!,
            method!.Value,
            parsed.SlaDays!.Value,
            contact!.Value,
            parsed.Active ?? true,
            pacing.MaxConcurrency ?? 1,
            pacing.MinDelayMs ?? 1000,
            pacing.RateLimitThreshold ?? 3,
            pacing.CooldownMinutes ?? 30,
            pacing.FormChangeThreshold ?? 3);
    }

    private static void ReadRecipe(
        IDeserializer header,
        string file,
        string yaml,
        BrokerRow? row,
        List<string> problems)
    {
        RecipeHeader? parsed;

        try
        {
            parsed = header.Deserialize<RecipeHeader>(yaml);
        }
        catch (YamlDotNet.Core.YamlException failure)
        {
            problems.Add($"{file}: not readable as YAML — {failure.Message}");

            return;
        }

        if (parsed?.BrokerId is null || parsed.BrokerId == Guid.Empty)
        {
            problems.Add($"{file}: names no brokerId.");

            return;
        }

        if (row is not null && parsed.BrokerId != row.Id)
        {
            problems.Add(
                $"{file}: is for {parsed.BrokerId}, and the company beside it is {row.Id}. A "
                + "recipe runs for the company it names, so this one would run for a different "
                + "company than the one it was reviewed with — or for none at all.");
        }
    }

    private static bool IsBareHost(string? domain) =>
        !string.IsNullOrWhiteSpace(domain)
        && domain.Contains('.', StringComparison.Ordinal)
        && !domain.Contains('/', StringComparison.Ordinal)
        && !domain.Contains('@', StringComparison.Ordinal)
        && !domain.Contains(':', StringComparison.Ordinal)
        && Uri.CheckHostName(domain) == UriHostNameType.Dns;

    private static bool IsCitation(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps;

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string FileOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? path : path[(slash + 1)..];
    }
}
