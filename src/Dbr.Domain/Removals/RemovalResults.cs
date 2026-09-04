// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Domain.Removals;

/// <summary>How an attempt to open a demand ended.</summary>
public enum OpenRemovalOutcome
{
    /// <summary>Recorded, and waiting for a worker.</summary>
    Opened,

    /// <summary>
    /// The tenant has not permitted removals, or has withdrawn that permission.
    /// </summary>
    /// <remarks>
    /// Checked at the moment of asking rather than at signup, which is the case a stale
    /// check gets wrong in the direction of speaking for somebody who told us to stop.
    /// </remarks>
    ConsentMissing,

    /// <summary>
    /// No such profile for this tenant.
    /// </summary>
    /// <remarks>
    /// One outcome for "no such profile" and "somebody else's profile", as everywhere
    /// else: telling them apart would confirm that an id belongs to another account.
    /// </remarks>
    ProfileNotFound,

    /// <summary>No such broker in this instance's catalog.</summary>
    UnknownBroker,

    /// <summary>
    /// The company accepts demands in a way this instance cannot send.
    /// </summary>
    /// <remarks>
    /// Post, today. Refused at the moment of asking rather than left to fail at dispatch,
    /// because the difference is what somebody is told: a request refused here is a company
    /// this service cannot help with, while one accepted and then stuck is a deadline
    /// running against a demand that was never sent.
    /// </remarks>
    UnsupportedRemovalMethod,

    /// <summary>No such listing for this tenant.</summary>
    ExposureNotFound,

    /// <summary>
    /// The cited listing was found for a different identity, or on a different company.
    /// </summary>
    /// <remarks>
    /// One outcome for both, because both are the same mistake seen from two sides: a
    /// demand citing evidence that is not about what the demand is about. The schema
    /// refuses each with its own composite key; this is the check that gets there first
    /// and can say something useful.
    /// </remarks>
    ExposureMismatch,

    /// <summary>
    /// The tenant has said this listing is not them.
    /// </summary>
    /// <remarks>
    /// The one judgement only the person can make, and it points the other way from a
    /// demand: sending one in somebody's name over a match they have told us is somebody
    /// else would be acting on a claim they withdrew.
    /// </remarks>
    ExposureDismissed,

    /// <summary>
    /// This identity already has a live demand of this kind open with this company.
    /// </summary>
    /// <remarks>
    /// Not an error to be worked around. Two open demands would send one company the same
    /// request twice in one person's name, and the lifecycle already loops on the single
    /// row — a listing that comes back reappears on the request that removed it.
    /// </remarks>
    AlreadyOpen,
}

/// <param name="Request">The demand, or <see langword="null"/> when none was opened.</param>
/// <param name="Broker">
/// The company it was addressed to, carried so the caller can name it without a second
/// read. Null whenever <paramref name="Request"/> is.
/// </param>
public sealed record OpenRemovalResult(
    OpenRemovalOutcome Outcome,
    RemovalRequest? Request,
    Broker? Broker)
{
    public static OpenRemovalResult Opened(RemovalRequest request, Broker broker) =>
        new(OpenRemovalOutcome.Opened, request, broker);

    public static OpenRemovalResult Failed(OpenRemovalOutcome outcome) => new(outcome, null, null);
}

/// <summary>How an attempt to move a demand by hand ended.</summary>
/// <remarks>
/// Shared by cancelling and retrying because the two ask the same question of the same
/// table — may this request move to that state — and differ only in which state. A pair of
/// near-identical enums would drift the first time one of them gained a case.
/// </remarks>
public enum MoveRemovalOutcome
{
    /// <summary>The request is now in the state that was asked for.</summary>
    Moved,

    /// <summary>No such request for this tenant.</summary>
    NotFound,

    /// <summary>
    /// The lifecycle does not allow this move from where the request currently is.
    /// </summary>
    /// <remarks>
    /// The refusal carries the table's own sentence rather than a code, because that
    /// sentence is written to be read by whoever asked — "a request that is removed cannot
    /// be cancelled" is actionable in a way that a status enum is not.
    /// </remarks>
    NotAllowed,

    /// <summary>
    /// The move is one the lifecycle allows and a guard refuses right now.
    /// </summary>
    /// <remarks>
    /// Retrying a request that has used its attempts, today. Separate from
    /// <see cref="NotAllowed"/> because the two are different answers: one says this can
    /// never happen from here, the other says it could have and the budget is spent.
    /// </remarks>
    Refused,
}

/// <param name="Request">The request as it now stands, or <see langword="null"/> when it did not move.</param>
/// <param name="Reason">
/// Why it did not move, in a sentence meant for whoever asked. Null when it did.
/// </param>
public sealed record MoveRemovalResult(
    MoveRemovalOutcome Outcome,
    RemovalRequest? Request,
    string? Reason)
{
    public static MoveRemovalResult Moved(RemovalRequest request) =>
        new(MoveRemovalOutcome.Moved, request, null);

    public static MoveRemovalResult NotFound() =>
        new(MoveRemovalOutcome.NotFound, null, null);

    public static MoveRemovalResult NotAllowed(string reason) =>
        new(MoveRemovalOutcome.NotAllowed, null, reason);

    public static MoveRemovalResult Refused(string reason) =>
        new(MoveRemovalOutcome.Refused, null, reason);
}

/// <summary>Which demands to list.</summary>
/// <param name="Status">
/// Only demands in this state, or <see langword="null"/> for all of them.
/// </param>
/// <param name="ProfileId">
/// Only demands made for this identity, or <see langword="null"/> for all of them. Here
/// because an account managing more than one identity has no other way to separate them,
/// and a list mixing a person's demands with their dependent's is one nobody can act on.
/// </param>
public sealed record RemovalFilter(RemovalRequestStatus? Status, Guid? ProfileId);

/// <summary>A demand and the company it is addressed to.</summary>
/// <remarks>
/// The pairing every read of this table wants. A demand names a company by id and nothing
/// showing one to somebody wants to show an id, so the two travel together rather than
/// leaving each caller to look the second one up.
/// </remarks>
public sealed record RemovalListing(RemovalRequest Request, Broker Broker);

/// <summary>
/// What has actually happened to one demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attempts, not the state transitions.</b> §6.5 asks this route for a full
/// transition history and there is nowhere to read one from: no table records that a
/// request went from queued to submitted, and the append-only audit log that will is
/// DBR-053. What exists is one row per attempt, which is a real history of the work — it
/// says how many times a company has been asked, by which connector, when, and whether
/// another try is scheduled.
/// </para>
/// <para>
/// The gap is worth naming rather than papering over: an attempt tells you the request was
/// dispatched, and nothing here can tell you when a broker answered or when a verification
/// scan confirmed a listing gone. Those become readable when there is a log of them.
/// </para>
/// </remarks>
public sealed record RemovalTimeline(
    RemovalRequest Request,
    Broker Broker,
    IReadOnlyList<RemovalJob> Attempts);
