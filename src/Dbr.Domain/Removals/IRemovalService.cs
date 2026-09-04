// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Domain.Removals;

/// <summary>
/// Opening demands, reading them, and the two a person can move by hand.
/// </summary>
/// <remarks>
/// <para>
/// Like the scan and profile services, every method acts for the tenant the current scope
/// was established for and none of them takes one. A caller that could name a tenant could
/// name the wrong one, and whose name a demand goes out in is not a decision to leave to an
/// argument.
/// </para>
/// <para>
/// <b>Nothing here dispatches.</b> Opening a demand records it and leaves it
/// <see cref="RemovalRequestStatus.Queued"/>; the lane that drains it exists and has no
/// consumer, so a message put on it now would sit in a queue nothing reads. The row is the
/// record, and the dispatcher is its own story.
/// </para>
/// <para>
/// <b>Cancel and retry are the only two moves offered.</b> Every other transition in the
/// lifecycle is something the system observes rather than something a person decides — a
/// broker accepting a demand, a deadline passing, a verification scan confirming a listing
/// gone. Exposing those as routes would let a client write a history that never happened.
/// </para>
/// </remarks>
public interface IRemovalService
{
    /// <summary>
    /// Opens a demand against one company on behalf of one of the tenant's identities.
    /// </summary>
    /// <param name="profileId">
    /// Whose data is being demanded gone, or <see langword="null"/> for the tenant's own
    /// identity. Always one of this account's existing profiles — there is no way to pass
    /// an identity here, which is the guardrail rather than a check standing in for one.
    /// </param>
    /// <param name="exposureId">
    /// The listing that prompted the demand, or <see langword="null"/> when none did.
    /// Optional because the right does not depend on it: a deletion request does not
    /// oblige somebody to prove a company holds their data, and an opt-out of sale is a
    /// meaningful thing to say to a company whose search page returns nothing today.
    /// </param>
    Task<OpenRemovalResult> OpenAsync(
        Guid? profileId,
        Guid brokerId,
        LegalRequestType requestType,
        Guid? exposureId,
        CancellationToken cancellationToken);

    /// <summary>Demands this account has opened, newest first.</summary>
    Task<IReadOnlyList<RemovalListing>> ListAsync(
        RemovalFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// One demand.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when there is no such request for this tenant, which covers
    /// both one that does not exist and one belonging to somebody else.
    /// </returns>
    Task<RemovalListing?> FindAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>What has been attempted on one demand, oldest attempt first.</summary>
    /// <returns><see langword="null"/> when there is no such request for this tenant.</returns>
    Task<RemovalTimeline?> TimelineAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Calls a demand off before it has been answered.
    /// </summary>
    /// <remarks>
    /// Allowed while a request is queued or submitted and not after, which is the
    /// lifecycle's rule rather than this method's. A demand a company has already answered
    /// cannot be unsent, and offering to cancel one would tell somebody that it had been.
    /// </remarks>
    Task<MoveRemovalResult> CancelAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a failed demand back to the queue.
    /// </summary>
    /// <remarks>
    /// By hand, and only from failed. The automatic path retries on its own schedule; this
    /// is for the person who has read why it failed, changed something, and wants another
    /// go without waiting.
    /// </remarks>
    Task<MoveRemovalResult> RetryAsync(Guid requestId, CancellationToken cancellationToken);
}
