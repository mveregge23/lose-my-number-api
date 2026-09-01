// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Search;

namespace Dbr.Search;

/// <summary>
/// Which companies this build knows how to search, and what runs for each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built once, from documents read once.</b> Recipes are catalog content: they arrive with
/// a deploy, and a deploy restarts the worker. Reading them per search would mean a file
/// changing under a running process, which is a way for two legs of one scan to search a
/// company differently — and there is no path by which a recipe appears without a deploy, so
/// nothing is bought by allowing for it.
/// </para>
/// <para>
/// <b>One engine instance per company, shared across every tenant's legs.</b> It holds a
/// recipe and an HTTP client and nothing about a person, so there is nothing in it to leak
/// between them. That is a property to keep rather than assume: the moment an identity is
/// parked on the engine, this sharing becomes a way to show somebody another account's
/// findings.
/// </para>
/// <para>
/// It answers nothing for a company it has no recipe for, which is most of the catalog and is
/// not a failure — the leg records that it was not searchable and the run carries on.
/// </para>
/// </remarks>
public sealed class RecipeSearchRegistry : IBrokerSearchRegistry
{
    private readonly IReadOnlyDictionary<Guid, IBrokerSearch> _searches;

    public RecipeSearchRegistry(
        IEnumerable<SearchRecipe> recipes,
        Func<SearchRecipe, IBrokerSearch> engine)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(engine);

        _searches = recipes.ToDictionary(recipe => recipe.BrokerId, engine);
    }

    /// <summary>How many companies this build can search.</summary>
    /// <remarks>
    /// For the line a composition root logs at startup. "Searching 4 of 412 companies" is the
    /// difference between a scan finding nothing because nobody is listed and a scan finding
    /// nothing because it asked almost nobody, and that is worth saying once a process starts
    /// rather than reconstructing from leg rows afterwards.
    /// </remarks>
    public int Count => _searches.Count;

    public IBrokerSearch? Find(Guid brokerId) =>
        _searches.TryGetValue(brokerId, out var search) ? search : null;
}
