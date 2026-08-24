// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// Which findings to list.
/// </summary>
/// <remarks>
/// Both filters are optional and combine, which is the question somebody actually has:
/// "what is still outstanding on this one company". Neither widens what comes back —
/// dismissed findings are in the table and are returned only when asked for by status,
/// because a list somebody is working through should not re-offer the ones they have
/// already said are not them.
/// </remarks>
/// <param name="Status">Only findings in this state.</param>
/// <param name="BrokerId">Only findings on this broker.</param>
public readonly record struct ExposureFilter(ExposureStatus? Status, Guid? BrokerId);

/// <summary>
/// One finding and the company it is on.
/// </summary>
/// <remarks>
/// The broker travels with the finding rather than being a second request. A listing is
/// unreadable without knowing whose site it is on, and a client made to resolve ids
/// against the catalog itself will either render an id to somebody or hold the list back
/// until a second call returns.
/// </remarks>
public sealed record ExposureListing(Exposure Exposure, Broker Broker);

/// <summary>How an attempt to dismiss a finding ended.</summary>
public enum DismissExposureOutcome
{
    /// <summary>Marked as not this person.</summary>
    Dismissed,

    /// <summary>Already dismissed, so nothing changed.</summary>
    /// <remarks>
    /// Answered the same way as <see cref="Dismissed"/>. The client asked for a state and
    /// the state holds; which call put it there is a fact about the history rather than
    /// about the answer, the same stance recording an unchanged consent decision takes.
    /// </remarks>
    AlreadyDismissed,

    /// <summary>No such finding for this tenant.</summary>
    NotFound,

    /// <summary>
    /// A removal request is open against it, so dismissing would contradict something
    /// already in flight.
    /// </summary>
    /// <remarks>
    /// Refused rather than allowed. Dismissing means "this is not me", and a request
    /// already sent in somebody's name over a listing they now disown is not resolved by
    /// changing a status column — it is resolved by cancelling the request, which is its
    /// own operation with its own consequences at the broker. Answering here would leave
    /// the contradiction in place and look like it had been dealt with.
    /// </remarks>
    RemovalInFlight,
}

/// <param name="Listing">
/// Where the finding stands afterwards, or <see langword="null"/> when nothing was
/// changed because it could not be found.
/// </param>
public sealed record DismissExposureResult(DismissExposureOutcome Outcome, ExposureListing? Listing)
{
    public static DismissExposureResult Failed(DismissExposureOutcome outcome) => new(outcome, null);
}
