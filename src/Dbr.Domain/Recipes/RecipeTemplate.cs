// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using System.Text.RegularExpressions;
using Dbr.Domain.Profiles;

namespace Dbr.Domain.Recipes;

/// <summary>
/// What a template is written against: the identity released for this attempt, and the
/// listing the demand is about, when there is one.
/// </summary>
/// <param name="Listing">
/// The listing that prompted the demand, or <see langword="null"/> when none did. A search
/// has none by definition — it is the thing that produces one — and a demand may have none,
/// since the right to be deleted does not depend on having been found first.
/// </param>
public sealed record RenderSubject(ProfileIdentityFields Identity, Uri? Listing);

/// <summary>
/// One thing a recipe may write into a query, and the group it comes out of.
/// </summary>
/// <param name="Path">How it is written between the braces.</param>
/// <param name="Field">
/// The group of the identity it reads, and therefore the group a recipe naming it causes to
/// be released — or <see langword="null"/> for the one kind of placeholder that reads the
/// demand rather than the identity. Those cause nothing to be decrypted that was not
/// already released for the attempt, and they are optional in prose: a demand with no
/// listing loses the line that would have cited one, rather than being refused.
/// </param>
/// <param name="Read">
/// How the value is got. Returns <see langword="null"/> when there is nothing there, which
/// is a real answer rather than a fault — a search that cannot run without a city and is
/// given a profile with no address is unsupported, not broken.
/// </param>
public sealed record RecipePlaceholder(
    string Path,
    IdentityField? Field,
    Func<RenderSubject, string?> Read)
{
    /// <summary>Whether this reads the demand rather than the identity.</summary>
    public bool ReadsDemand => Field is null;
}

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
    private static string? FirstName(RenderSubject subject) =>
        subject.Identity.Names.Count > 0 ? subject.Identity.Names[0] : null;

    private static string? Address(RenderSubject subject, Func<ProfileAddress, string?> read) =>
        subject.Identity.Addresses.Count > 0 ? read(subject.Identity.Addresses[0]) : null;

    private static string? Contact(RenderSubject subject, ProfileContactKind kind) =>
        subject.Identity.Contacts.FirstOrDefault(contact => contact.Kind == kind)?.Value;

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
                subject => Part(FirstName(subject), first: true)),
            new RecipePlaceholder(
                "names.last",
                IdentityField.Names,
                subject => Part(FirstName(subject), first: false)),

            new RecipePlaceholder(
                "addresses.first.line1",
                IdentityField.Addresses,
                subject => Address(subject, address => address.Line1)),
            new RecipePlaceholder(
                "addresses.first.city",
                IdentityField.Addresses,
                subject => Address(subject, address => address.City)),
            new RecipePlaceholder(
                "addresses.first.region",
                IdentityField.Addresses,
                subject => Address(subject, address => address.Region)),
            new RecipePlaceholder(
                "addresses.first.postalCode",
                IdentityField.Addresses,
                subject => Address(subject, address => address.PostalCode)),

            new RecipePlaceholder(
                "contacts.email",
                IdentityField.Contacts,
                subject => Contact(subject, ProfileContactKind.Email)),
            new RecipePlaceholder(
                "contacts.phone",
                IdentityField.Contacts,
                subject => Contact(subject, ProfileContactKind.Phone)),

            new RecipePlaceholder(
                "dateOfBirth.year",
                IdentityField.DateOfBirth,
                subject => subject.Identity.DateOfBirth?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)),

            // The listing the demand is about: the one thing a demand may cite that is not
            // part of the identity. A company's own opt-out flow usually asks for exactly
            // this, and it names the record more precisely than a name and a city can. It
            // reads the demand, so it releases nothing — the listing was already released
            // to the attempt that is composing this — and it is optional in prose, because
            // a demand made without having found anything is a legitimate demand.
            new RecipePlaceholder(
                "listing.url",
                null,
                subject => subject.Listing?.ToString()),
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

    /// <summary>
    /// Whether this template writes the listing a demand is about.
    /// </summary>
    /// <remarks>
    /// Meaningful to a demand and meaningless to a search, which is the thing that produces
    /// a listing and so cannot cite one. A search recipe is refused for it at read time.
    /// </remarks>
    public bool CitesListing => _parts.OfType<RecipePlaceholder>().Any(placeholder => placeholder.ReadsDemand);

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

            if (placeholder.Field is { } field)
            {
                fields.Add(field);
            }
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
    /// Writes the identity into a query, escaping every value it puts there.
    /// </summary>
    /// <remarks>
    /// <b>The literal halves are left alone and the values are escaped.</b> A name with an
    /// ampersand in it would otherwise end one query parameter and begin another, which is a
    /// bug that only shows up for the people whose names contain one — and a recipe author
    /// escaping by hand would be escaping the punctuation they wrote as well.
    /// </remarks>
    public RenderResult RenderQuery(ProfileIdentityFields identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // A query has no listing to cite and no lines to drop: a demand placeholder here is
        // a search recipe the reader should have refused, and it renders as missing rather
        // than as an empty parameter that searches for nobody in particular.
        return Render(new RenderSubject(identity, null), Uri.EscapeDataString, dropLines: false);
    }

    /// <summary>
    /// Writes the identity, and the listing if there is one, into prose exactly as held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is escaped, and that is the difference from
    /// <see cref="RenderQuery"/>.</b> A demand is read by a person, so a name with an
    /// ampersand in it has to arrive as that name rather than as <c>%26</c> — and there is
    /// no punctuation here that a value could break out of, because the result is not
    /// parsed by anything. The two destinations are named separately rather than sharing a
    /// method with a flag, so that neither can be reached by forgetting to pass one.
    /// </para>
    /// <para>
    /// <b>A line citing a listing the demand does not have is dropped whole.</b> The
    /// alternative readings are worse: refusing the demand would make a listing a
    /// precondition of asking to be deleted, and rendering the line with a hole in it would
    /// send a company "Listing: " and nothing after. The rule is the line rather than the
    /// placeholder, because the words around a citation are about the citation.
    /// </para>
    /// </remarks>
    public RenderResult RenderText(RenderSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return Render(subject, value => value, dropLines: true);
    }

    /// <summary>Prose from an identity alone, for the templates and tests that cite nothing.</summary>
    public RenderResult RenderText(ProfileIdentityFields identity) =>
        RenderText(new RenderSubject(identity, null));

    /// <summary>Marks where an absent listing would have gone, so its line can be found.</summary>
    private const char Absent = '\uE000';

    private RenderResult Render(RenderSubject subject, Func<string, string> escape, bool dropLines)
    {
        var built = new StringBuilder();

        foreach (var part in _parts)
        {
            if (part is string literal)
            {
                built.Append(literal);

                continue;
            }

            var placeholder = (RecipePlaceholder)part;
            var value = placeholder.Read(subject);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (placeholder.ReadsDemand && dropLines)
                {
                    built.Append(Absent);

                    continue;
                }

                // Not an error and not an empty query. A search that needs a city and is
                // given a profile with no address on file cannot do what this attempt asks
                // of it, which is a specific answer the contract already has a name for.
                return RenderResult.NothingFor(placeholder.Path);
            }

            built.Append(escape(value));
        }

        var rendered = built.ToString();

        if (rendered.Contains(Absent, StringComparison.Ordinal))
        {
            rendered = string.Join(
                '\n',
                rendered.Split('\n').Where(line => !line.Contains(Absent, StringComparison.Ordinal)));
        }

        return RenderResult.Rendered(rendered);
    }
}
