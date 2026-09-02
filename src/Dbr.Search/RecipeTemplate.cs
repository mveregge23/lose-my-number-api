// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using System.Text.RegularExpressions;
using Dbr.Domain.Profiles;

namespace Dbr.Search;

/// <summary>
/// One thing a recipe may write into a query, and the group it comes out of.
/// </summary>
/// <param name="Path">How it is written between the braces.</param>
/// <param name="Field">The group of the identity it reads, and therefore the group a
/// recipe naming it causes to be released.</param>
/// <param name="Read">
/// How the value is got. Returns <see langword="null"/> when the profile has nothing there,
/// which is a real answer rather than a fault — a search that cannot run without a city and
/// is given a profile with no address is unsupported, not broken.
/// </param>
public sealed record RecipePlaceholder(
    string Path,
    IdentityField Field,
    Func<ProfileIdentityFields, string?> Read);

/// <summary>
/// The closed set of things a recipe may say.
/// </summary>
/// <remarks>
/// <para>
/// <b>A vocabulary rather than an expression language, and that is the security boundary.</b>
/// A recipe is contributed data reviewed at a lighter bar than code precisely because there
/// is nothing in it to execute. The moment a placeholder could be an arbitrary path into an
/// object graph, reviewing one would mean reasoning about what that path can reach — which
/// is the code bar, arrived at by accident.
/// </para>
/// <para>
/// <b>Every entry names the group it reads, and that is what builds the release.</b> The
/// groups a recipe mentions are the groups its grant covers, worked out from the document
/// before the engine runs. A recipe that never writes a date of birth cannot cause one to be
/// decrypted — not because nothing asks at the wrong moment, but because there is no moment
/// at which it could.
/// </para>
/// <para>
/// <b>Position, not currency.</b> §9.7 writes <c>addresses.current.line1</c>, and a profile
/// does not carry the distinction: addresses are stored current and former together, with no
/// flag saying which is which, because an old address is often the only reason a listing is
/// findable at all. Spelling it <c>current</c> here would promise a distinction the model
/// cannot make, so these name a position instead and the recipe author is told what that
/// means. Whichever address is first is the one a query is built from.
/// </para>
/// </remarks>
public static class RecipeVocabulary
{
    private static string? FirstName(ProfileIdentityFields identity) =>
        identity.Names.Count > 0 ? identity.Names[0] : null;

    private static string? Address(ProfileIdentityFields identity, Func<ProfileAddress, string?> read) =>
        identity.Addresses.Count > 0 ? read(identity.Addresses[0]) : null;

    private static string? Contact(ProfileIdentityFields identity, ProfileContactKind kind) =>
        identity.Contacts.FirstOrDefault(contact => contact.Kind == kind)?.Value;

    /// <summary>Every placeholder a recipe may use, by the path it is written as.</summary>
    public static IReadOnlyDictionary<string, RecipePlaceholder> All { get; } =
        new[]
        {
            new RecipePlaceholder("names.full", IdentityField.Names, FirstName),

            // Split on the last space, which is wrong for a minority of names and is what a
            // search box asking for two fields forces. A recipe should prefer names.full
            // wherever the site accepts one string.
            new RecipePlaceholder(
                "names.first",
                IdentityField.Names,
                identity => Part(FirstName(identity), first: true)),
            new RecipePlaceholder(
                "names.last",
                IdentityField.Names,
                identity => Part(FirstName(identity), first: false)),

            new RecipePlaceholder(
                "addresses.first.line1",
                IdentityField.Addresses,
                identity => Address(identity, address => address.Line1)),
            new RecipePlaceholder(
                "addresses.first.city",
                IdentityField.Addresses,
                identity => Address(identity, address => address.City)),
            new RecipePlaceholder(
                "addresses.first.region",
                IdentityField.Addresses,
                identity => Address(identity, address => address.Region)),
            new RecipePlaceholder(
                "addresses.first.postalCode",
                IdentityField.Addresses,
                identity => Address(identity, address => address.PostalCode)),

            new RecipePlaceholder(
                "contacts.email",
                IdentityField.Contacts,
                identity => Contact(identity, ProfileContactKind.Email)),
            new RecipePlaceholder(
                "contacts.phone",
                IdentityField.Contacts,
                identity => Contact(identity, ProfileContactKind.Phone)),

            new RecipePlaceholder(
                "dateOfBirth.year",
                IdentityField.DateOfBirth,
                identity => identity.DateOfBirth?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        }.ToDictionary(placeholder => placeholder.Path, StringComparer.Ordinal);

    private static string? Part(string? name, bool first)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');

        if (space < 0)
        {
            // One word. It is the whole name either way, and calling it a surname is the
            // less wrong of the two.
            return first ? null : trimmed;
        }

        return first ? trimmed[..space] : trimmed[(space + 1)..];
    }
}

/// <param name="Missing">
/// The placeholder the profile had nothing for, when that is why nothing was rendered.
/// </param>
public sealed record RenderResult(string? Value, string? Missing)
{
    public static RenderResult Rendered(string value) => new(value, null);

    public static RenderResult NothingFor(string placeholder) => new(null, placeholder);
}

/// <summary>
/// A piece of a query with the identity written into it.
/// </summary>
/// <remarks>
/// <b>Parsed once, when the recipe is read, and not at every search.</b> That is what makes
/// an unknown placeholder a review failure rather than a runtime one, and it is what lets
/// the groups a recipe needs be known before anything runs — the whole arrangement in §9.7
/// depends on the document being readable without executing it.
/// </remarks>
public sealed partial class RecipeTemplate
{
    private readonly IReadOnlyList<object> _parts;

    private RecipeTemplate(
        string raw,
        IReadOnlyList<object> parts,
        IReadOnlySet<IdentityField> requiredFields)
    {
        Raw = raw;
        _parts = parts;
        RequiredFields = requiredFields;
    }

    /// <summary>The template as written.</summary>
    public string Raw { get; }

    /// <summary>The groups of an identity this template reads.</summary>
    public IReadOnlySet<IdentityField> RequiredFields { get; }

    [GeneratedRegex(@"\{\{\s*([^}]*?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>
    /// Reads a template, or says what is wrong with it.
    /// </summary>
    /// <param name="problem">
    /// Why it cannot be used, or <see langword="null"/>. A sentence rather than an
    /// exception, because a recipe is reviewed as a document and every problem in it should
    /// arrive at once.
    /// </param>
    public static RecipeTemplate? TryParse(string raw, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var parts = new List<object>();
        var fields = new HashSet<IdentityField>();
        var at = 0;

        foreach (Match match in Placeholder().Matches(raw))
        {
            if (match.Index > at)
            {
                parts.Add(raw[at..match.Index]);
            }

            var path = match.Groups[1].Value;

            if (!RecipeVocabulary.All.TryGetValue(path, out var placeholder))
            {
                problem =
                    $"'{{{{{path}}}}}' is not something a recipe can write. The whole list is "
                    + $"{string.Join(", ", RecipeVocabulary.All.Keys.Order(StringComparer.Ordinal))} "
                    + "— a recipe is data precisely because there is nothing in it to "
                    + "interpret beyond that list.";

                return null;
            }

            parts.Add(placeholder);
            fields.Add(placeholder.Field);
            at = match.Index + match.Length;
        }

        if (at < raw.Length)
        {
            parts.Add(raw[at..]);
        }

        problem = null;

        return new RecipeTemplate(raw, parts, fields);
    }

    /// <summary>
    /// Writes the identity into the template, escaping every value it puts there.
    /// </summary>
    /// <remarks>
    /// <b>The literal halves are left alone and the values are escaped.</b> A name with an
    /// ampersand in it would otherwise end one query parameter and begin another, which is a
    /// bug that only shows up for the people whose names contain one — and a recipe author
    /// escaping by hand would be escaping the punctuation they wrote as well.
    /// </remarks>
    public RenderResult Render(ProfileIdentityFields identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var built = new StringBuilder();

        foreach (var part in _parts)
        {
            if (part is string literal)
            {
                built.Append(literal);

                continue;
            }

            var placeholder = (RecipePlaceholder)part;
            var value = placeholder.Read(identity);

            if (string.IsNullOrWhiteSpace(value))
            {
                // Not an error and not an empty query. A search that needs a city and is
                // given a profile with no address on file cannot do what this attempt asks
                // of it, which is a specific answer the contract already has a name for.
                return RenderResult.NothingFor(placeholder.Path);
            }

            built.Append(Uri.EscapeDataString(value));
        }

        return RenderResult.Rendered(built.ToString());
    }
}
