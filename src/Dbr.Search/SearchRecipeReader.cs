// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using AngleSharp.Css.Parser;
using Dbr.Domain.Profiles;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dbr.Search;

/// <param name="Recipes">Every recipe, when nothing is wrong with it.</param>
/// <param name="Problems">
/// Everything wrong, rather than the first thing wrong. The same policy the legal-basis and
/// fixture readers keep: a validator that stops at the first error turns one review into
/// four, and a recipe is reviewed as a document.
/// </param>
public sealed record RecipeReadResult(
    IReadOnlyList<SearchRecipe> Recipes,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads the search recipes and says whether they are usable.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half that runs without a network</b>, which is what makes it the check CI
/// can perform on a pull request. A recipe with a selector that is not a selector, or a
/// placeholder that is not a placeholder, should fail review rather than fail a scan months
/// later against a company that then sees a burst of malformed requests.
/// </para>
/// <para>
/// <b>Selectors are parsed rather than pattern-matched.</b> Checking that a string looks
/// selector-ish would accept most of what a typo produces; handing it to the same parser that
/// will run it is the only check that means anything, and it costs one call.
/// </para>
/// </remarks>
public static class SearchRecipeReader
{
    /// <summary>Where recipes sit relative to whatever is reading them.</summary>
    /// <remarks>
    /// Beside the fixtures they are dry-run against, which is what §21.4 asks for: a recipe
    /// and the recorded pages that exercise it are reviewed together and arrive in one pull
    /// request.
    /// </remarks>
    public const string DefaultRoot = "catalog/brokers";

    private const string RecipeName = "search.yaml";

    /// <summary>Reads every recipe from beside the running assembly.</summary>
    public static RecipeReadResult Read() =>
        Read(Path.Combine(AppContext.BaseDirectory, DefaultRoot));

    /// <summary>Reads every recipe under one directory.</summary>
    public static RecipeReadResult Read(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return new RecipeReadResult(
                [],
                [$"There is no recipe directory at '{root}'."]);
        }

        var recipes = new List<SearchRecipe>();
        var problems = new List<string>();
        var brokers = new Dictionary<Guid, string>();

        var directories = Directory
            .EnumerateDirectories(root)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var path = Path.Combine(directory, RecipeName);

            if (!File.Exists(path))
            {
                // Not a problem. Most companies in the catalog will have recorded pages and
                // no recipe for a long time, and a directory without one is exactly how
                // "nothing knows how to search this company" is written down.
                continue;
            }

            var recipe = ReadOne(name, File.ReadAllText(path), problems);

            if (recipe is null)
            {
                continue;
            }

            if (brokers.TryGetValue(recipe.BrokerId, out var already))
            {
                problems.Add(
                    $"'{name}' and '{already}' both claim broker {recipe.BrokerId}. A company "
                    + "with two recipes is one whose searches depend on which file was read "
                    + "first.");

                continue;
            }

            brokers[recipe.BrokerId] = name;
            recipes.Add(recipe);
        }

        return new RecipeReadResult(recipes, problems);
    }

    /// <summary>Reads one recipe document, for a test or for a caller holding the text.</summary>
    public static SearchRecipe? ReadOne(string name, string yaml, List<string> problems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(problems);

        RecipeFile? file;

        try
        {
            file = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<RecipeFile>(yaml);
        }
        catch (YamlException exception)
        {
            problems.Add($"'{name}' has a {RecipeName} that is not valid YAML: {exception.Message}");

            return null;
        }

        if (file is null)
        {
            problems.Add($"'{name}' has an empty {RecipeName}.");

            return null;
        }

        var before = problems.Count;

        if (!Guid.TryParse(file.BrokerId, out var brokerId) || brokerId == Guid.Empty)
        {
            problems.Add(
                $"'{name}' names broker '{file.BrokerId}', which is not an id. A recipe is bound "
                + "to a catalog row by id rather than by domain, because a domain is a field "
                + "somebody corrects and the correction would silently unbind it.");
        }

        if (string.IsNullOrWhiteSpace(file.Description))
        {
            problems.Add($"'{name}' does not say what it searches or how.");
        }

        var query = ReadQuery(name, file.Query, problems);

        var item = Selector(name, "item", file.Item, problems);
        var link = Selector(name, "link", file.Link, problems);
        var noResults = Selector(name, "noResults", file.NoResults, problems);

        string? blocked = null;

        if (!string.IsNullOrWhiteSpace(file.Blocked))
        {
            blocked = Selector(name, "blocked", file.Blocked, problems);
        }

        var fields = ReadFields(name, file.Fields, problems);

        if (problems.Count != before)
        {
            return null;
        }

        return new SearchRecipe(
            brokerId,
            name,
            file.Description!,
            query!,
            item!,
            link!,
            noResults!,
            blocked,
            fields);
    }

    private static RecipeTemplate? ReadQuery(string name, string? raw, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            problems.Add($"'{name}' has no query, so there is nothing to ask the company for.");

            return null;
        }

        // The one refusal that is about what a contributed document must not be able to do,
        // rather than about it being wrong. A recipe naming where the request goes is a recipe
        // that can send somebody's name anywhere.
        //
        // Both forms have to be refused, and the second is the one that is easy to miss. A
        // query beginning "//" is protocol-relative: it contains no scheme at all, so a check
        // for "://" waves it through, and resolving it against an origin keeps the scheme and
        // replaces the host. "//somewhere-else.test/collect" reads like a path and is not one.
        if (raw.Contains("://", StringComparison.Ordinal)
            || raw.StartsWith("//", StringComparison.Ordinal))
        {
            problems.Add(
                $"'{name}' has a query naming a whole address. A recipe writes a path and a "
                + "query string; where the request goes comes from the company's catalog row, "
                + "so that a change to this document can never redirect somebody's identity "
                + "somewhere else.");

            return null;
        }

        if (!raw.StartsWith('/'))
        {
            // A relative query resolves against the origin's path rather than its root, which
            // is merely surprising today and would stop being harmless the moment an origin
            // carried one. Requiring the leading slash makes what it asks for unambiguous.
            problems.Add(
                $"'{name}' has a query that does not begin with '/'. A query is a path on the "
                + "company's own site, and one written relatively resolves against wherever the "
                + "engine happens to be pointed.");

            return null;
        }

        var template = RecipeTemplate.TryParse(raw, out var problem);

        if (template is null)
        {
            problems.Add($"'{name}': {problem}");

            return null;
        }

        if (template.RequiredFields.Count == 0)
        {
            problems.Add(
                $"'{name}' has a query that writes no part of an identity into it, so it would "
                + "ask the same question whoever it was searching for.");

            return null;
        }

        return template;
    }

    private static IReadOnlyList<RecipeFieldSelector> ReadFields(
        string name,
        Dictionary<string, string>? declared,
        List<string> problems)
    {
        var fields = new List<RecipeFieldSelector>();
        var seen = new HashSet<IdentityField>();

        foreach (var (group, selector) in declared ?? [])
        {
            var field = IdentityVocabulary.Parse(group) ?? ParseCamel(group);

            if (field is null)
            {
                problems.Add(
                    $"'{name}' compares against '{group}', which is not a group of an identity. "
                    + "The groups are names, addresses and contacts.");

                continue;
            }

            if (field is IdentityField.DateOfBirth)
            {
                // Refused rather than half-implemented. A listing shows an age far more often
                // than a date, and turning one into the other needs a "today" that a recipe
                // cannot see and a tolerance nobody has decided on. A recipe may still write
                // a year into its query, which is a different thing: writing a number is not
                // deciding whether two dates are the same person.
                problems.Add(
                    $"'{name}' compares a listing against a date of birth. Pages show an age "
                    + "rather than a date, and what an age agrees with is a judgement nothing "
                    + "here has made yet — so this is refused rather than guessed at.");

                continue;
            }

            if (!seen.Add(field.Value))
            {
                problems.Add($"'{name}' compares against {group} twice.");

                continue;
            }

            var parsed = Selector(name, group, selector, problems);

            if (parsed is not null)
            {
                fields.Add(new RecipeFieldSelector(field.Value, parsed));
            }
        }

        if (fields.Count == 0)
        {
            problems.Add(
                $"'{name}' compares a listing against nothing, so every finding it reported "
                + "would claim a listing and give no reason to think it is this person.");
        }

        return fields;
    }

    /// <summary>The wire spelling, and the camelCase a YAML key naturally takes.</summary>
    private static IdentityField? ParseCamel(string group) => group switch
    {
        "dateOfBirth" => IdentityField.DateOfBirth,
        _ => null,
    };

    /// <summary>
    /// A selector, checked by the parser that will run it.
    /// </summary>
    private static string? Selector(string name, string what, string? selector, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            problems.Add($"'{name}' has no {what} selector.");

            return null;
        }

        try
        {
            var parser = new CssSelectorParser();
            var parsed = parser.ParseSelector(selector);

            if (parsed is null)
            {
                problems.Add($"'{name}' has a {what} selector that is not one: '{selector}'.");

                return null;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            problems.Add($"'{name}' has a {what} selector that is not one: '{selector}'.");

            return null;
        }

        return selector;
    }

    // The YAML shape, kept private. What a file may say and what a recipe is are different
    // questions, and one type answering both makes every property nullable for the parser.
    private sealed class RecipeFile
    {
        public string? BrokerId { get; set; }

        public string? Description { get; set; }

        public string? Query { get; set; }

        public string? Item { get; set; }

        public string? Link { get; set; }

        public string? NoResults { get; set; }

        public string? Blocked { get; set; }

        public Dictionary<string, string>? Fields { get; set; }
    }
}
