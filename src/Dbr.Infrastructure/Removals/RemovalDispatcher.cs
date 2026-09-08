// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Messaging;
using Dbr.Domain.Removals;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// Claims a queued demand, writes the attempt, and puts it in its company's lane.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is what makes two dispatchers safe.</b> A demand moves out of
/// <see cref="RemovalRequestStatus.Queued"/> in one conditional statement, and only the
/// caller that changed the row goes on to send it. Reading the status and then writing it
/// would leave the window every duplicate-dispatch bug lives in — and the symptom here is a
/// company receiving the same demand twice in one person's name, which is the thing the
/// shared lanes and the uniqueness index both exist to prevent.
/// </para>
/// <para>
/// <b>The attempt is written before the message is sent</b>, so a message that arrives
/// first finds a row to record against. The cost is the reverse gap: a process that dies
/// between the write and the send leaves an attempt nothing will pick up, and the demand
/// sits submitted. That is a real hole and it is the ordinary one — an outbox is what
/// closes it, and this story does not build one, exactly as the scan side does not.
/// </para>
/// <para>
/// <b>A demand that cannot be sent stays queued rather than failing.</b> This is where a
/// removal and a scan genuinely differ. A scan is a run that has to finish, so a leg nothing
/// can search is written as finished having searched nothing. A demand is not a run — it is
/// a standing request that a company be asked, and "no connector for this company yet" is a
/// true description of it still waiting. Failing it would spend an attempt on this
/// instance's build rather than on the company, and eventually expire a demand nobody ever
/// sent.
/// </para>
/// </remarks>
public sealed class RemovalDispatcher(
    DbrDbContext core,
    IBrokerConnectorRegistry connectors,
    IIdentityReleaseMinter minter,
    IBrokerWorkDispatcher lanes,
    TimeProvider clock,
    ILogger<RemovalDispatcher> logger)
    : IRemovalDispatcher
{
    public async Task<RemovalDispatchResult> DispatchAsync(
        Guid removalRequestId,
        CancellationToken cancellationToken)
    {
        var request = await core.Set<RemovalRequest>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == removalRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null || request.Status != RemovalRequestStatus.Queued)
        {
            return RemovalDispatchResult.Failed(RemovalDispatchOutcome.NotClaimable);
        }

        // Read before the claim, so that a demand nothing can carry is left exactly as it
        // was found. Claiming first and rolling back would leave the same row in the same
        // state and a wasted transaction between them.
        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.BrokerId, cancellationToken)
            .ConfigureAwait(false);

        if (broker is not { Active: true })
        {
            logger.LogInformation(
                "Removal request {RequestId} is for broker {BrokerId}, which this instance does "
                + "not dispatch against. Leaving it queued.",
                removalRequestId,
                request.BrokerId);

            return RemovalDispatchResult.Failed(RemovalDispatchOutcome.BrokerNotDispatchable);
        }

        if (connectors.Find(request.BrokerId) is not { } registration)
        {
            logger.LogInformation(
                "Removal request {RequestId} is for broker {BrokerId}, which this build has no "
                + "connector for. Leaving it queued.",
                removalRequestId,
                request.BrokerId);

            return RemovalDispatchResult.Failed(RemovalDispatchOutcome.NoConnector);
        }

        if (ConnectorContract.Refuse(registration) is { } unusable)
        {
            // A registration whose name could never be written down. Caught here rather
            // than at the insert, which is the difference between refusing to send and
            // discovering it after a company has already been asked.
            logger.LogError(
                "The connector registered for broker {BrokerId} cannot be recorded: {Refusal} "
                + "Removal request {RequestId} stays queued.",
                request.BrokerId,
                unusable,
                removalRequestId);

            return RemovalDispatchResult.Failed(RemovalDispatchOutcome.NoConnector);
        }

        var now = clock.GetUtcNow();
        var attemptNumber = request.Attempt + 1;

        var claimed = await core.Set<RemovalRequest>()
            .Where(row => row.Id == removalRequestId && row.Status == RemovalRequestStatus.Queued)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Status, RemovalRequestStatus.Submitted)
                    .SetProperty(row => row.Attempt, attemptNumber),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed != 1)
        {
            return RemovalDispatchResult.Failed(RemovalDispatchOutcome.NotClaimable);
        }

        var job = new RemovalJob
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            RemovalRequestId = request.Id,
            ConnectorId = registration.ConnectorId,
            Status = RemovalJobStatus.Pending,
            AttemptNumber = attemptNumber,
            RunAt = now,
        };

        core.Set<RemovalJob>().Add(job);

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Minted after the attempt exists, because the grant is scoped to it. The
        // declaration is what the grant covers, so a connector that never names a date of
        // birth cannot cause one to be decrypted — not because nothing asks at the wrong
        // moment, but because there is no moment at which it could.
        var minted = await minter
            .MintForJobAsync(
                job.Id,
                request.BrokerId,
                registration.Connector.Capabilities.RequiredFields,
                cancellationToken)
            .ConfigureAwait(false);

        if (minted.Release is null)
        {
            // The attempt exists and cannot be carried out. Recorded as a failed attempt
            // rather than left pending, because a message is never going to arrive for it —
            // and the demand goes back to the queue so the next pass mints a fresh grant.
            job.Status = RemovalJobStatus.Failed;
            job.FailureReason = ConnectorFailureReason.Unsupported;
            job.Detail = $"No grant could be minted for this attempt: {minted.Outcome}.";

            await core.Set<RemovalRequest>()
                .Where(row => row.Id == removalRequestId)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(row => row.Status, RemovalRequestStatus.Queued),
                    cancellationToken)
                .ConfigureAwait(false);

            await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                "Removal request {RequestId} could not mint a grant for broker {BrokerId}: "
                + "{Outcome}. It stays queued.",
                removalRequestId,
                request.BrokerId,
                minted.Outcome);

            return RemovalDispatchResult.Failed(RemovalDispatchOutcome.ReleaseRefused);
        }

        var work = new RemovalJobWork(
            request.Id,
            job.Id,
            request.TenantId,
            request.BrokerId,
            request.PrivacyProfileId,
            attemptNumber,
            minted.Release.Token);

        await lanes.DispatchAsync(work, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Removal request {RequestId} dispatched to broker {BrokerId} as attempt {Attempt} "
            + "using connector {ConnectorId}.",
            removalRequestId,
            request.BrokerId,
            attemptNumber,
            registration.ConnectorId);

        return RemovalDispatchResult.Dispatched(work);
    }
}
