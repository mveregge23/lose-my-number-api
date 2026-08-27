// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Domain.Search;

/// <summary>How closely one group of an identity agreed with what a listing showed.</summary>
/// <remarks>
/// Three values because two would lose the useful one. <see cref="Conflicting"/> is not a
/// failed match — it is a match on something that turned out to be somebody else, and a
/// listing that agrees on a name while disagreeing on every address on file is a very
/// different candidate from one that agrees on a name and shows nothing else. Collapsing
/// the two into "no match" would make them indistinguishable to whatever decides what is
/// worth showing.
/// </remarks>
public enum MatchStrength
{
    /// <summary>The listing showed the same thing.</summary>
    Exact,

    /// <summary>
    /// Close enough to be the same thing, and not the same thing.
    /// </summary>
    /// <remarks>
    /// A surname with a middle initial the profile does not carry, a street the profile
    /// has at a different number, a city without its state. What counts as close is the
    /// search's judgement about the page it read; what it means is not.
    /// </remarks>
    Partial,

    /// <summary>The listing showed something incompatible.</summary>
    Conflicting,
}

/// <summary>
/// What one group of the identity did, on one listing.
/// </summary>
/// <remarks>
/// <b>There is nowhere here to put what the listing said.</b> That is deliberate: the text
/// on a broker's profile page is that broker's copy of somebody's identity, and carrying it
/// back would copy it into a result the worker logs, retries and puts in a queue — turning
/// a search for a person's data into a second store of it. The field and how closely it
/// agreed is everything the answer needs, and it is not identifying on its own.
/// </remarks>
public sealed record FieldMatch(IdentityField Field, MatchStrength Strength);

/// <summary>
/// One listing that might be this person.
/// </summary>
/// <remarks>
/// <para>
/// Might, not is. A candidate is an observation — this page exists, and these parts of the
/// identity agreed with it to this degree. Whether that is enough to put in front of
/// somebody as their data is a decision made above this line, against a bar that has to
/// mean the same thing whichever broker produced the candidate.
/// </para>
/// <para>
/// <see cref="SourceRef"/> is restricted in the same way a name is, and for a plainer
/// reason than it looks: a broker's profile URL routinely contains the name, the city and
/// sometimes the age of the person it is about, so the link is a copy of the identity
/// rather than a pointer to one. It is withheld from <see cref="ToString"/> here for the
/// same reason the identity fields are withheld from theirs — a record prints every member
/// it has, which puts this one interpolation away from being in a log line.
/// </para>
/// </remarks>
public sealed record SearchCandidate(Uri SourceRef, IReadOnlyList<FieldMatch> Matches)
{
    /// <inheritdoc cref="ProfileIdentityFields.ToString"/>
    public override string ToString() =>
        $"SearchCandidate {{ Matches = {Matches.Count}, [withheld] }}";
}

/// <summary>
/// Why a search could not answer.
/// </summary>
/// <remarks>
/// The removal side's taxonomy, minus the one that has no meaning here and plus the one
/// that does. A broker declining a request is a removal outcome — a search asks nothing of
/// anybody and cannot be declined — while being turned away at the door by a bot wall is
/// specific to reading a site that did not invite you.
/// </remarks>
public enum SearchFailureReason
{
    /// <summary>A timeout, a connection reset, a 5xx. Nothing about this request in particular.</summary>
    Transient,

    /// <summary>The broker throttled this instance and said so.</summary>
    RateLimited,

    /// <summary>
    /// The page was reachable and no longer looks like what the search expects.
    /// </summary>
    /// <remarks>
    /// Distinct from a transient failure because retrying cannot help and because it is the
    /// one failure that is a message to whoever maintains the catalog rather than to the
    /// worker. A search that reported this as transient would burn every attempt and leave
    /// the entry looking flaky rather than stale.
    /// </remarks>
    PageShapeChanged,

    /// <summary>The broker refused to serve this instance at all.</summary>
    /// <remarks>
    /// A bot wall, a challenge page, an address-level block. Separate from
    /// <see cref="RateLimited"/> because waiting longer is the answer to one and not to the
    /// other, and separate from <see cref="PageShapeChanged"/> because nothing about the
    /// catalog entry is wrong.
    /// </remarks>
    Blocked,

    /// <summary>The search cannot do what this attempt asks of it.</summary>
    /// <remarks>
    /// A configuration fault rather than a runtime one — an identity missing a field the
    /// search cannot work without, a site variant it does not handle. It says the wiring is
    /// wrong, so retrying the same wiring is pointless.
    /// </remarks>
    Unsupported,
}

/// <summary>
/// What one search of one broker came back with.
/// </summary>
/// <remarks>
/// <para>
/// A closed hierarchy: the three cases below are the only ones there are, because the base
/// constructor is private and only its own nested types can reach it. A search compiled
/// against this cannot invent a fourth outcome the worker has no branch for, and the
/// worker's switch over these is exhaustive by construction rather than by a default case
/// that quietly means "something else happened".
/// </para>
/// <para>
/// <b><see cref="NothingFound"/> is not a failure.</b> A broker that was reached and holds
/// nothing about this person is the outcome everybody involved is hoping for, and reporting
/// it as an error would make a clean answer indistinguishable from an unanswered one. The
/// reverse conflation is worse and is why <see cref="Found"/> is never empty: a result
/// carrying no candidates would be that same clean answer wearing the shape of a finding.
/// </para>
/// </remarks>
public abstract record SearchResult
{
    private SearchResult()
    {
    }

    /// <summary>Listings that might be this person, at least one.</summary>
    public sealed record Found(IReadOnlyList<SearchCandidate> Candidates) : SearchResult;

    /// <summary>The broker answered, and holds nothing about this person.</summary>
    public sealed record NothingFound : SearchResult;

    /// <summary>
    /// The broker did not answer the question.
    /// </summary>
    /// <param name="Detail">
    /// What actually happened, for whoever reads the log. Never the identity that was being
    /// searched for, and never the page's content — a status line, a selector that did not
    /// match, the name of the timeout that expired.
    /// </param>
    /// <param name="Retryable">
    /// The search's own call, not the worker's. The reason narrows it — nothing retries a
    /// changed page — but within a reason the search is the only thing that saw what
    /// happened: a connection reset and a host that no longer resolves are both transient
    /// by category, and only one of them is worth another attempt.
    /// </param>
    public sealed record Failed(
        SearchFailureReason Reason,
        string Detail,
        bool Retryable) : SearchResult;
}
