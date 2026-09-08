// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Removals;

/// <param name="TenantId">The account to act as in order to send it.</param>
public sealed record QueuedRemoval(Guid RemovalRequestId, Guid TenantId);

/// <summary>
/// Which demands are waiting, and whose.
/// </summary>
/// <remarks>
/// The third question no tenant-scoped role can answer, and the narrowest of them. A demand
/// is recorded as queued by the account that opened it; what sends it is a process acting
/// for nobody in particular, which has to find out what is waiting before it can act for
/// anybody.
/// <para>
/// <b>It sees waiting demands and nothing else.</b> Not a dispatched one, not a finished
/// one, and nothing about whose data any of them is about. So it cannot be used to watch
/// what an account is doing — only to find work nobody has picked up.
/// </para>
/// </remarks>
public interface IQueuedRemovalDirectory
{
    /// <summary>Demands that have been opened and not yet sent.</summary>
    /// <param name="limit">
    /// How many to take. A sweep is a batch rather than a drain: an instance with a backlog
    /// should make progress on each pass and leave the rest, so one wake-up after an outage
    /// does not put every waiting demand into every lane at once.
    /// </param>
    Task<IReadOnlyList<QueuedRemoval>> ListQueuedAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>How an attempt to send one demand ended.</summary>
public enum RemovalDispatchOutcome
{
    /// <summary>Claimed, written as an attempt, and put in its company's lane.</summary>
    Dispatched,

    /// <summary>
    /// Somebody else got there first, or it is no longer queued.
    /// </summary>
    /// <remarks>
    /// The ordinary answer when two dispatchers run, and not a failure. The claim is one
    /// conditional statement, so exactly one caller can win it.
    /// </remarks>
    NotClaimable,

    /// <summary>
    /// This build has no connector for the company, so nothing could carry the demand.
    /// </summary>
    /// <remarks>
    /// <b>The demand stays queued rather than failing.</b> This is where a removal and a
    /// scan differ, and the difference is real: a scan is a run that has to finish, so a leg
    /// nothing can search is recorded as finished-having-searched-nothing. A demand is not a
    /// run — it is a standing request that a company be asked, and it is perfectly true to
    /// say it has not been asked yet. Failing it would spend an attempt on the catalog
    /// rather than on the company, and expire a demand nobody ever sent.
    /// </remarks>
    NoConnector,

    /// <summary>
    /// The attempt exists and no grant could be minted for it.
    /// </summary>
    /// <remarks>
    /// Distinct from having no connector, because the two say different things: one is a
    /// company this build cannot ask, the other is a fault in the release path — a demand
    /// that moved on while it was being dispatched, or a company the attempt is not
    /// addressed to. The attempt is recorded as failed and the demand returns to the queue,
    /// since a message will never arrive for a grant that was never issued.
    /// </remarks>
    ReleaseRefused,

    /// <summary>
    /// The company is no longer one this instance dispatches against.
    /// </summary>
    /// <remarks>
    /// Deactivated or gone from the catalog between the demand being opened and picked up.
    /// The demand stays queued for the same reason as above: an operator turning a company
    /// off is a statement about this instance, not an answer to the person waiting.
    /// </remarks>
    BrokerNotDispatchable,
}

/// <param name="Work">What was put in the lane, or <see langword="null"/> when nothing was.</param>
public sealed record RemovalDispatchResult(RemovalDispatchOutcome Outcome, RemovalJobWork? Work)
{
    public static RemovalDispatchResult Dispatched(RemovalJobWork work) =>
        new(RemovalDispatchOutcome.Dispatched, work);

    public static RemovalDispatchResult Failed(RemovalDispatchOutcome outcome) => new(outcome, null);
}

/// <summary>
/// Claims a queued demand, writes the attempt, and puts it in its company's lane.
/// </summary>
/// <remarks>
/// The mirror of what the scan dispatcher does, with one structural difference: a scan fans
/// out to every company in its scope, and a demand is addressed to exactly one. So there is
/// no planning step here and no partial success — a demand is either sent or it is still
/// waiting.
/// </remarks>
public interface IRemovalDispatcher
{
    /// <summary>Sends one demand, if it is still there to send.</summary>
    Task<RemovalDispatchResult> DispatchAsync(Guid removalRequestId, CancellationToken cancellationToken);
}
