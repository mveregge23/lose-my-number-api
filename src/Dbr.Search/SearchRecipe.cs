// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Search;

/// <summary>Which part of a listing a selector reads, and what it is compared against.</summary>
/// <param name="Selector">
/// A CSS selector, relative to one result block. Read as text, because what a listing said is
/// deliberately never carried back — only which group it agreed with and how closely.
/// </param>
public sealed record RecipeFieldSelector(IdentityField Field, string Selector);

/// <summary>
/// How to ask one company what it holds, written as a document.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is nowhere here to put a host, and that is the point.</b> A recipe is contributed
/// data reviewed at a lighter bar than code (§9.1), so the worst a bad one can do has to be
/// small. A document that could name where the request goes would be one that could send
/// somebody's name to any address on the internet, reviewed as "this YAML now points at a
/// different URL". What it names is a path and a query; the company comes from the catalog
/// row, which is reviewed as catalog content and cannot be changed by a recipe.
/// </para>
/// <para>
/// <b>The groups it needs are derived, never declared.</b> <see cref="RequiredFields"/> is
/// the union of what the query writes and what the results are compared against, worked out
/// from the document itself — so it cannot disagree with what the recipe actually uses, which
/// is the whole reason the recipe tier can be trusted with a release at all.
/// </para>
/// </remarks>
/// <param name="BrokerId">
/// The catalog's identity for the company this reads. On the recipe rather than the catalog
/// row pointing back, following §9.7 — and by id rather than by domain, for the reason lanes
/// are named by id: a domain is a field somebody corrects, and a recipe bound to one would be
/// silently unbound by the correction.
/// </param>
/// <param name="Query">
/// The path and query string to ask for, with the identity written into it. Relative on
/// purpose; see the note above.
/// </param>
/// <param name="Item">A CSS selector matching one listing on the results page.</param>
/// <param name="Link">
/// A CSS selector, relative to a listing, whose <c>href</c> points at that listing's own page.
/// This is what becomes the source reference, and there is no fallback: a finding that cannot
/// say which page it was found on is one nobody can check or act on.
/// </param>
/// <param name="NoResults">
/// A CSS selector present only when the company holds nothing. Required, and it is the most
/// load-bearing line in the document: without it, a page whose results container has been
/// renamed and a page that genuinely lists nobody are the same absence — and reporting the
/// first as the second tells somebody they are not listed anywhere on the strength of a
/// changed class name.
/// </param>
/// <param name="Blocked">
/// A CSS selector present when a challenge page has been served instead of results, or
/// <see langword="null"/>. Optional because most refusals arrive as a status code; here for
/// the common case of a wall served with 200, which no status could distinguish from a page
/// holding nothing.
/// </param>
public sealed record SearchRecipe(
    Guid BrokerId,
    string Name,
    string Description,
    RecipeTemplate Query,
    string Item,
    string Link,
    string NoResults,
    string? Blocked,
    IReadOnlyList<RecipeFieldSelector> Fields)
{
    /// <summary>
    /// Everything this recipe needs released, and nothing else.
    /// </summary>
    /// <remarks>
    /// Both halves, which is easy to get wrong by taking only the first. The query is the
    /// obvious one. The comparisons matter just as much: a recipe that searches by name and
    /// then reports whether the listing's address agreed has to have been given the address
    /// to compare, and a finding claiming a match on a group the search never held is refused
    /// by the contract — correctly, and confusingly, if this had left the group out.
    /// </remarks>
    public IReadOnlySet<IdentityField> RequiredFields { get; } =
        Query.RequiredFields
            .Concat(Fields.Select(field => field.Field))
            .ToHashSet();

    /// <summary>What the engine declares to the worker before it is invoked.</summary>
    public SearchCapabilities Capabilities => new(SearchKind.Recipe, RequiredFields);
}
