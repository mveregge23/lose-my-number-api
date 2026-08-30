// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Messaging;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Search;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Claims a queued scan and puts one piece of work into each company's lane.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is what makes two dispatchers safe.</b> A run moves out of
/// <see cref="ScanStatus.Queued"/> in one conditional statement, and only the caller that
/// changed the row goes on to plan it. Reading the status and then writing it would leave
/// the window every duplicate-dispatch bug lives in — and the symptom would be a company
/// asked twice about one person, which is precisely what the shared lanes exist to
/// prevent.
/// </para>
/// <para>
/// <b>The legs are written before any of them is sent.</b> A message that arrived before
/// its row existed would find nothing to record against, and the run would never be able
/// to say it had finished. The cost of that ordering is the reverse gap: a process that
/// dies between the write and the send leaves a leg planned that nothing will ever pick
/// up, and the run stays under way. That is a real hole and it is the ordinary one — an
/// outbox is what closes it, and this story does not build one.
/// </para>
/// <para>
/// <b>A leg that cannot run is a row, not a silence.</b> No search in this build for that
/// company, or a grant the run was not entitled to: either way the row is written with its
/// outcome already on it. A scan covering forty companies and able to search four should
/// say exactly that, and it can only say it if the thirty-six are somewhere.
/// </para>
/// </remarks>
public sealed class ScanDispatcher(
    DbrDbContext core,
    IBrokerSearchRegistry searches,
    IIdentityReleaseMinter minter,
    IBrokerWorkDispatcher lanes,
    ScanCompletion completion,
    TimeProvider clock,
    ILogger<ScanDispatcher> logger)
    : IScanDispatcher
{
    public async Task<ScanDispatchResult> DispatchAsync(
        Guid scanId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var claimed = await core.Set<Scan>()
            .Where(scan => scan.Id == scanId && scan.Status == ScanStatus.Queued)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(scan => scan.Status, ScanStatus.Running)
                    .SetProperty(scan => scan.StartedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed != 1)
        {
            return ScanDispatchResult.NotClaimable();
        }

        var scan = await core.Set<Scan>()
            .AsNoTracking()
            .FirstAsync(row => row.Id == scanId, cancellationToken)
            .ConfigureAwait(false);

        var brokers = await InScopeAsync(scanId, cancellationToken).ConfigureAwait(false);

        var sendable = new List<ScanBrokerWork>();
        var unplannable = 0;

        foreach (var broker in brokers)
        {
            var work = await PlanAsync(scan, broker, now, cancellationToken).ConfigureAwait(false);

            if (work is null)
            {
                unplannable++;
                continue;
            }

            sendable.Add(work);
        }

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var work in sendable)
        {
            await lanes.DispatchAsync(work, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Scan {ScanId} started: {Planned} legs queued, {Unplannable} could not be planned, "
            + "across {BrokerCount} companies in scope.",
            scanId,
            sendable.Count,
            unplannable,
            brokers.Count);

        if (brokers.Count == 0)
        {
            // Nobody to ask. The run is over rather than stuck, and it is over having done
            // what it set out to do — settled here because there is no leg coming that
            // would otherwise ask the question.
            await completion.TrySettleAsync(scanId, cancellationToken).ConfigureAwait(false);

            return new ScanDispatchResult(ScanDispatchOutcome.NothingInScope, 0, 0);
        }

        if (sendable.Count == 0)
        {
            // Every leg was over before it was sent, so nothing will arrive to notice that
            // the run has finished.
            await completion.TrySettleAsync(scanId, cancellationToken).ConfigureAwait(false);
        }

        return new ScanDispatchResult(ScanDispatchOutcome.Started, sendable.Count, unplannable);
    }

    /// <summary>
    /// Writes this company's leg, and returns the work to send when there is any.
    /// </summary>
    /// <remarks>
    /// The two refusals are deliberately different rows rather than one "could not run".
    /// A company nothing knows how to search is the ordinary state of most of the catalog
    /// and says nothing is wrong; a grant that would not mint says the run and the catalog
    /// disagree about what may be asked, which is a fault worth finding.
    /// </remarks>
    private async Task<ScanBrokerWork?> PlanAsync(
        Scan scan,
        Guid brokerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var leg = new ScanLeg
        {
            TenantId = scan.TenantId,
            ScanId = scan.Id,
            BrokerId = brokerId,
            AttemptNumber = 1,
            PlannedAt = now,
        };

        core.Set<ScanLeg>().Add(leg);

        var search = searches.Find(brokerId);

        if (search is null)
        {
            leg.Outcome = ScanLegOutcome.NoSearchAvailable;
            leg.CompletedAt = now;
            leg.Detail = "This build has no search for this company.";

            return null;
        }

        // The declaration is what the grant is minted for, so a search that never names a
        // date of birth cannot cause one to be decrypted — not because nothing asks at the
        // wrong moment, but because there is no moment at which it could.
        var minted = await minter
            .MintAsync(scan.Id, brokerId, search.Capabilities.RequiredFields, cancellationToken)
            .ConfigureAwait(false);

        if (minted.Release is null)
        {
            leg.Outcome = ScanLegOutcome.ReleaseRefused;
            leg.CompletedAt = now;

            // The outcome and not the identity: which company, and which of the reasons a
            // grant is refused. Nothing here is about the person being searched for.
            leg.Detail = $"No grant could be minted for this leg: {minted.Outcome}.";

            logger.LogWarning(
                "Scan {ScanId} could not mint a grant for broker {BrokerId}: {Outcome}.",
                scan.Id,
                brokerId,
                minted.Outcome);

            return null;
        }

        return new ScanBrokerWork(
            scan.Id,
            scan.TenantId,
            brokerId,
            scan.PrivacyProfileId,
            minted.Release.Token,
            leg.AttemptNumber);
    }

    /// <summary>
    /// Which companies this run covers.
    /// </summary>
    /// <remarks>
    /// No narrowing rows means the whole catalog rather than nothing, which is the reading
    /// the scan table and its migration both record. Inactive entries are left out of both
    /// cases: an entry an operator has deactivated is one this instance has decided not to
    /// dispatch against, and a tenant who named it when it was active asked for a company
    /// rather than for a lane that no longer exists.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> InScopeAsync(
        Guid scanId,
        CancellationToken cancellationToken)
    {
        var narrowed = await core.Set<ScanBroker>()
            .AsNoTracking()
            .Where(row => row.ScanId == scanId)
            .Select(row => row.BrokerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var active = core.Set<Broker>().AsNoTracking().Where(broker => broker.Active);

        if (narrowed.Count > 0)
        {
            active = active.Where(broker => narrowed.Contains(broker.Id));
        }

        // Ordered so that two runs of the same scope plan their legs in the same order,
        // which is what makes one dispatch log readable against another.
        return await active
            .Select(broker => broker.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
