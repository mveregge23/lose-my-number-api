// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Removals;

/// <summary>
/// How each company talks, as that company's own mailbox document declares it.
/// </summary>
/// <remarks>
/// Beside the recipes rather than beside the reading, because these are facts about a
/// company of exactly the kind the rest of its file holds — read from its boilerplate,
/// reviewed at the same bar, and changed by a pull request when the company changes its
/// wording.
/// </remarks>
public sealed class CatalogReplyPhrases : IBrokerReplyPhrases
{
    private readonly IReadOnlyDictionary<Guid, ReplyPhrases> _phrases;

    public CatalogReplyPhrases(IEnumerable<EmailRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);

        _phrases = recipes
            .Where(recipe => !recipe.Replies.IsEmpty)
            .ToDictionary(recipe => recipe.BrokerId, recipe => recipe.Replies);
    }

    /// <summary>How many companies this build can read an answer from.</summary>
    /// <remarks>
    /// For the line a composition root logs at startup, beside the count of companies it can
    /// ask. The two are deliberately different numbers: a company can be written to long
    /// before anybody has read enough of its answers to say how it talks, and the gap
    /// between them is how many companies' replies land in front of a person.
    /// </remarks>
    public int Count => _phrases.Count;

    public ReplyPhrases For(Guid brokerId) =>
        _phrases.TryGetValue(brokerId, out var phrases) ? phrases : ReplyPhrases.None;
}
