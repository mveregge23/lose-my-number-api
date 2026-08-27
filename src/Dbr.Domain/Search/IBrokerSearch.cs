// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Domain.Search;

/// <summary>Which review bar an implementation was held to.</summary>
/// <remarks>
/// The distinction is not cosmetic and it is not about quality. A recipe is a document a
/// generic engine interprets — it can be linted, diffed and merged by somebody who never
/// reads C#, and the worst a bad one does is fail one broker's searches. A code search is
/// a class running in the worker process with whatever identity that search was released,
/// so it is reviewed like any other change to the worker and lives in a curated list
/// rather than being discovered. Recording which one a search is keeps that difference
/// legible at dispatch, where the decision to run it is actually made.
/// </remarks>
public enum SearchKind
{
    /// <summary>A declarative document, interpreted by a generic engine.</summary>
    Recipe,

    /// <summary>A hand-written class, allow-listed and compiled in.</summary>
    Code,
}

/// <summary>
/// What a search needs before it can run.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiredFields"/> is a declaration, not a request: it is read before the
/// search is invoked and it is what the release asks the vault for. A search that never
/// names a date of birth therefore cannot cause one to be decrypted — not because nothing
/// asks for it at the wrong moment, but because there is no moment at which it could. The
/// same list is what bounds the answer: a finding may only claim to have matched on a
/// field named here, since a search cannot recognise something it was never given.
/// </para>
/// <para>
/// For a recipe the list is derived from the document — whichever placeholders it
/// references — so it cannot disagree with what the recipe actually uses. For a code
/// search it is written out and reviewed alongside the class.
/// </para>
/// </remarks>
/// <param name="RequiredFields">
/// The groups of an identity this search compares against, and nothing beyond them.
/// Never empty: a search that needs no part of an identity is not searching for anybody.
/// </param>
public sealed record SearchCapabilities(SearchKind Kind, IReadOnlySet<IdentityField> RequiredFields);

/// <summary>
/// Asks one broker what it holds about one identity.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the connector contract, and mirrored rather than copied. A connector is
/// handed a removal request and the listing it is about, and acts on them; a search has
/// neither, because neither exists yet — <b>this is the thing that produces a listing
/// reference, so it cannot be given one</b>. That asymmetry is the whole reason the
/// removal contract could not simply be widened: a search pointed at a listing it had
/// already found would be a verification, which is a different question with a different
/// answer.
/// </para>
/// <para>
/// <b>It reports what it saw, and does not judge it.</b> A finding carries which parts of
/// the identity the listing appeared to agree with and how closely, and no score. Scoring
/// belongs above this line because the floor below which nothing is shown to somebody has
/// to mean the same thing on every broker, and it cannot if four hundred separately
/// contributed searches each invent a number for it.
/// </para>
/// <para>
/// <b>Nothing here names a tenant.</b> A search is given an identity to look for and the
/// company to look at, and has no way to ask whose identity it is. Whatever it does with
/// the page it fetches, it cannot attribute the result to an account, and it cannot reach
/// back for anything it was not handed.
/// </para>
/// <para>
/// <b>Throwing is not an answer.</b> An implementation that cannot say what happened
/// should return <see cref="SearchResult.Failed"/> with a reason, which is what tells the
/// worker whether trying again is worth anything. An exception escaping here is a bug in
/// the search, and it is treated as one rather than as a broker that was quiet today.
/// </para>
/// </remarks>
public interface IBrokerSearch
{
    /// <summary>What this search needs, read before it is invoked.</summary>
    SearchCapabilities Capabilities { get; }

    /// <summary>Looks, once, and says what was there.</summary>
    Task<SearchResult> SearchAsync(SearchContext context, CancellationToken cancellationToken);
}
