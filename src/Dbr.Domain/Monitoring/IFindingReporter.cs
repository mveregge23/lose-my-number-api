// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Search;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// One listing a leg wants recorded.
/// </summary>
/// <remarks>
/// The candidate a search produced, unchanged and unscored. Scoring happens where the
/// findings are written rather than where they are reported, so the floor is applied by the
/// process that keeps the data and not by the one that talks to broker sites — which means a
/// worker cannot decide that something below the bar is worth keeping.
/// </remarks>
public sealed record ReportedListing(Uri SourceRef, IReadOnlyList<FieldMatch> Matches)
{
    /// <inheritdoc cref="SearchCandidate.ToString"/>
    public override string ToString() =>
        $"ReportedListing {{ Matches = {Matches.Count}, [withheld] }}";
}

/// <summary>How an attempt to record a leg's findings ended.</summary>
public enum ReportFindingsOutcome
{
    /// <summary>Recorded, and the grant is spent for reporting.</summary>
    Recorded,

    /// <summary>
    /// Not a grant that can report now.
    /// </summary>
    /// <remarks>
    /// One outcome for every refusal, as redeeming has: a token that was never minted, one
    /// that expired, and one whose findings were already recorded are the same answer to
    /// whoever holds it. What the difference is good for is the log.
    /// </remarks>
    Refused,
}

/// <param name="Recorded">Listings that cleared the bar and became findings.</param>
/// <param name="BelowFloor">
/// Listings that did not. A count rather than rows, because an exposure nobody will ever be
/// shown is a durable record of a weak claim about somebody — and the count is still what
/// tells an operator a bar is set wrong.
/// </param>
public sealed record ReportFindingsResult(
    ReportFindingsOutcome Outcome,
    int Recorded,
    int BelowFloor)
{
    public static ReportFindingsResult Refused() => new(ReportFindingsOutcome.Refused, 0, 0);
}

/// <summary>
/// Writing down what a leg found, on behalf of something that cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mirror of redeeming a release, one step later in the same leg.</b> A finding now
/// carries the listing's address, which is a copy of somebody's identity and belongs in the
/// vault — so the process that finds listings can no longer write one, for the same reason it
/// cannot read a name. It presents the grant it was given and hands over what it saw.
/// </para>
/// <para>
/// <b>It takes a token and no tenant.</b> Like redeeming, it is called on behalf of something
/// acting for nobody; the grant is what establishes whose findings these are, so there is no
/// argument that could make them somebody else's.
/// </para>
/// <para>
/// <b>The floor is applied here.</b> A leg reports what it saw and does not decide what is
/// worth keeping — that bar has to mean the same thing whichever company produced the
/// candidate, and a worker applying it would be a worker that could choose not to.
/// </para>
/// </remarks>
public interface IFindingReporter
{
    /// <summary>
    /// Records a leg's findings, once.
    /// </summary>
    /// <remarks>
    /// Single-use in its own right: the grant's reporting spend is claimed in the same
    /// statement that checks it, so a redelivered message does not file a second copy of
    /// everything. Separate from the release spend, so opening the identity does not consume
    /// the right to say what it found.
    /// </remarks>
    Task<ReportFindingsResult> ReportAsync(
        string token,
        IReadOnlyList<ReportedListing> listings,
        CancellationToken cancellationToken);
}
