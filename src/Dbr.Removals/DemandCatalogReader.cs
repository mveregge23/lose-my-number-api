// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Recipes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dbr.Removals;

/// <param name="Recipes">Which company takes a demand at which mailbox.</param>
/// <param name="Templates">What a demand says, per act and per right invoked.</param>
/// <param name="Problems">
/// Everything wrong, rather than the first thing wrong. The same policy the search-recipe,
/// legal-basis and fixture readers keep: a validator that stops at the first error turns one
/// review into four, and these are reviewed as documents.
/// </param>
public sealed record DemandCatalogReadResult(
    IReadOnlyList<EmailRecipe> Recipes,
    IReadOnlyList<DemandTemplate> Templates,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads the mailboxes and the wording, and says whether they are usable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half that runs without a network</b>, which is what makes it the check CI can
/// perform on a pull request. Wording with a placeholder that is not a placeholder should
/// fail review rather than fail a demand months later, in front of a company, on behalf of
/// somebody who is waiting.
/// </para>
/// <para>
/// <b>Read once, at startup, and not per demand.</b> These arrive with a deploy and a deploy
/// restarts the worker, so reading them per attempt would only make it possible for two
/// attempts at one demand to be worded differently.
/// </para>
/// </remarks>
public static class DemandCatalogReader
{
    /// <summary>Where the per-company mailboxes sit, relative to whatever is reading them.</summary>
    public const string DefaultRecipeRoot = "catalog/brokers";

    /// <summary>Where the wording sits.</summary>
    /// <remarks>
    /// Under the legal-basis directory rather than beside the recipes, and that placement is
    /// the point: it puts the sentences this service says on somebody's behalf behind the
    /// same review as the deadlines, which is a higher bar than a company's mailbox needs.
    /// A wrong mailbox fails visibly and costs one attempt. A sentence claiming a right that
    /// does not exist where somebody lives misinforms them about their legal position, and
    /// nothing about it looks wrong.
    /// </remarks>
    public const string DefaultTemplateRoot = "catalog/legal-basis/templates";

    private const string RecipeName = "email.yaml";

    /// <summary>Reads everything from beside the running assembly.</summary>
    public static DemandCatalogReadResult Read() =>
        Read(
            Path.Combine(AppContext.BaseDirectory, DefaultRecipeRoot),
            Path.Combine(AppContext.BaseDirectory, DefaultTemplateRoot));

    /// <summary>Reads the mailboxes under one directory and the wording under another.</summary>
    public static DemandCatalogReadResult Read(string recipeRoot, string templateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateRoot);

        var problems = new List<string>();
        var recipes = ReadRecipes(recipeRoot, problems);
        var templates = ReadTemplates(templateRoot, problems);

        return new DemandCatalogReadResult(recipes, templates, problems);
    }

    private static IReadOnlyList<EmailRecipe> ReadRecipes(string root, List<string> problems)
    {
        var recipes = new List<EmailRecipe>();

        if (!Directory.Exists(root))
        {
            // Not a problem. A build that carries no email recipes asks no company by mail,
            // which is the honest state of things while they are still being written.
            return recipes;
        }

        var seen = new Dictionary<Guid, string>();

        foreach (var path in Directory
            .EnumerateFiles(root, RecipeName, SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
            var recipe = ReadRecipe(name, path, problems);

            if (recipe is null)
            {
                continue;
            }

            if (seen.TryGetValue(recipe.BrokerId, out var already))
            {
                problems.Add(
                    $"'{name}' and '{already}' both claim broker {recipe.BrokerId}. One company "
                    + "cannot have two mailboxes here, and which one a demand went to would "
                    + "depend on the order the directory happened to be read in.");

                continue;
            }

            seen[recipe.BrokerId] = name;
            recipes.Add(recipe);
        }

        return recipes;
    }

    private static EmailRecipe? ReadRecipe(string name, string path, List<string> problems)
    {
        var file = Parse<EmailRecipeFile>(name, path, problems);

        if (file is null)
        {
            return null;
        }

        if (!Guid.TryParse(file.BrokerId, out var brokerId) || brokerId == Guid.Empty)
        {
            problems.Add(
                $"'{name}' does not name a broker by id. A recipe is bound by id and not by "
                + "domain, because a domain is a field somebody corrects and the correction "
                + "would silently unbind it.");

            return null;
        }

        if (string.IsNullOrWhiteSpace(file.Mailbox))
        {
            problems.Add($"'{name}' names no mailbox, so there is nowhere to send a demand.");

            return null;
        }

        var mailbox = file.Mailbox.Trim();

        // The refusal that is about what a contributed document must not be able to do,
        // rather than about it being wrong. The domain comes from the company's catalog row;
        // a document that could write a whole address could send somebody's name and home
        // address to any mailbox on the internet, and it would arrive for review looking
        // like a changed string.
        if (mailbox.Contains('@', StringComparison.Ordinal))
        {
            problems.Add(
                $"'{name}' writes a whole address. A recipe writes the local part only — the "
                + "'privacy' in 'privacy@example.com' — and the domain comes from the "
                + "company's catalog row, so that a change to this document can never send "
                + "somebody's identity to a different company.");

            return null;
        }

        if (mailbox.Any(character => char.IsWhiteSpace(character) || character is ',' or '<' or '>'))
        {
            problems.Add(
                $"'{name}' has a mailbox with punctuation no local part can carry: "
                + $"'{mailbox}'. A second recipient, or a name wrapped in angle brackets, is "
                + "a demand delivered somewhere nobody reviewed.");

            return null;
        }

        return new EmailRecipe(brokerId, mailbox);
    }

    private static IReadOnlyList<DemandTemplate> ReadTemplates(string root, List<string> problems)
    {
        var templates = new List<DemandTemplate>();

        if (!Directory.Exists(root))
        {
            return templates;
        }

        var seen = new Dictionary<DemandTemplateKey, string>();

        foreach (var path in Directory
            .EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var template = ReadTemplate(name, path, problems);

            if (template is null)
            {
                continue;
            }

            if (seen.TryGetValue(template.Key, out var already))
            {
                problems.Add(
                    $"'{name}' and '{already}' both word the same demand. Which wording went "
                    + "out would depend on the order the directory happened to be read in, "
                    + "which is not something a reviewer can check.");

                continue;
            }

            seen[template.Key] = name;
            templates.Add(template);
        }

        return templates;
    }

    private static DemandTemplate? ReadTemplate(string name, string path, List<string> problems)
    {
        var file = Parse<DemandTemplateFile>(name, path, problems);

        if (file is null)
        {
            return null;
        }

        var before = problems.Count;

        // The catalog's own spelling, read by the catalog's own parser. A second one here
        // would be a second opinion about what 'opt_out_sale' means, and the day they
        // disagreed the wording would be filed under a right nobody invoked.
        var requestType = CatalogVocabulary.ParseLegalRequestType(file.RequestType?.Trim());

        if (requestType is null)
        {
            problems.Add(
                $"'{name}' does not say which right it invokes. A demand to delete and a "
                + "demand to opt out of a sale are different claims, and "
                + $"'{file.RequestType}' names neither — it is 'delete', 'opt_out_sale' or "
                + "'opt_out_targeted_ads'.");
        }

        // A statute and its citation travel together or not at all, which is the same rule
        // the contract holds a demand to. Wording that names an act and gives nowhere to
        // read it is an assertion rather than a reference; wording that cites nothing is a
        // request rather than an instruction, and both are legitimate — what is not is one
        // half of either.
        var hasCode = !string.IsNullOrWhiteSpace(file.StatuteCode);

        if (hasCode && string.IsNullOrWhiteSpace(file.CitationUrl))
        {
            problems.Add(
                $"'{name}' words a demand under {file.StatuteCode} and gives nowhere to read "
                + "it. A citation somebody cannot follow is an assertion rather than a "
                + "reference, and it is the reviewer's only way to check that the sentences "
                + "below say what the act says.");
        }

        if (!hasCode && !string.IsNullOrWhiteSpace(file.CitationUrl))
        {
            problems.Add(
                $"'{name}' cites something and names no act. Wording that cites nothing is a "
                + "request rather than an instruction, which is legitimate — half of a "
                + "citation is not.");
        }

        // Mandatory rather than optional metadata, and it is the one rule here that is not
        // about the document being well-formed. Wording with a wrong deadline in it fails the
        // way a wrong selector does: visibly, once. Wording that claims a right somebody does
        // not have misinforms them about their legal position and looks entirely fine doing
        // it, which is why this content is held to who read it rather than only to what it
        // says.
        if (string.IsNullOrWhiteSpace(file.ReviewedBy))
        {
            problems.Add(
                $"'{name}' names nobody who reviewed it. These are the sentences this service "
                + "says on somebody else's behalf, and an unattributed one is a claim nobody "
                + "has taken responsibility for.");
        }

        if (string.IsNullOrWhiteSpace(file.Subject))
        {
            problems.Add($"'{name}' has no subject, so the demand arrives unlabelled.");
        }

        if (string.IsNullOrWhiteSpace(file.Body))
        {
            problems.Add($"'{name}' has no body, so the demand says nothing.");
        }

        var subject = ReadTemplateText(name, "subject", file.Subject, problems);
        var body = ReadTemplateText(name, "body", file.Body, problems);

        if (problems.Count != before)
        {
            return null;
        }

        var key = hasCode
            ? DemandTemplateKey.For(file.StatuteCode!.Trim(), requestType!.Value)
            : DemandTemplateKey.Courtesy(requestType!.Value);

        return new DemandTemplate(key, subject!, body!);
    }

    private static RecipeTemplate? ReadTemplateText(
        string name,
        string field,
        string? raw,
        List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var template = RecipeTemplate.TryParse(raw, out var problem);

        if (template is null)
        {
            problems.Add($"'{name}' has a {field} that cannot be used. {problem}");
        }

        return template;
    }

    private static T? Parse<T>(string name, string path, List<string> problems)
        where T : class
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            var parsed = deserializer.Deserialize<T>(File.ReadAllText(path));

            if (parsed is null)
            {
                problems.Add($"'{name}' is empty.");
            }

            return parsed;
        }
        catch (YamlException exception)
        {
            problems.Add($"'{name}' is not readable as YAML: {exception.Message}");

            return null;
        }
    }

    private sealed class EmailRecipeFile
    {
        public string? BrokerId { get; set; }

        public string? Mailbox { get; set; }

        public string? Description { get; set; }
    }

    private sealed class DemandTemplateFile
    {
        public string? StatuteCode { get; set; }

        public string? RequestType { get; set; }

        public string? CitationUrl { get; set; }

        public string? ReviewedBy { get; set; }

        public string? ReviewedAt { get; set; }

        public string? Subject { get; set; }

        public string? Body { get; set; }
    }
}
