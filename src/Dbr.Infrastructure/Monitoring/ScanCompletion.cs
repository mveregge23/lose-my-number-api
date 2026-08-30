// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Decides whether a run is over, from the rows rather than from whoever is asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is present at the end of a scan.</b> The legs run independently, each in its
/// own lane, each whenever its company may next be spoken to — so the thing that dispatched
/// them is long gone, and the last leg to finish has no way of knowing it was the last. So
/// every leg asks, and the answer comes from counting the rows that are still unfinished.
/// </para>
/// <para>
/// <b>Two legs finishing together both ask, and that is fine.</b> They read the same rows
/// and reach the same verdict, and the write is conditional on the run still being under
/// way — so one of them moves it and the other changes nothing. The alternative, a lock
/// held across the check and the write, would serialise every leg of every scan on the
/// instance in order to tidy up a race whose two outcomes are identical.
/// </para>
/// <para>
/// Concrete rather than behind an interface, like the release lookup: it is one query and
/// one conditional update against this schema, and an interface would suggest there is
/// another way to answer the question.
/// </para>
/// </remarks>
public sealed class ScanCompletion(DbrDbContext core, TimeProvider clock)
{
    /// <summary>
    /// Ends the run if every leg has finished, and does nothing if any has not.
    /// </summary>
    /// <returns>
    /// The status the run was moved to, or <see langword="null"/> when it was left alone —
    /// either because a leg is still going or because somebody else got there first.
    /// </returns>
    public async Task<ScanStatus?> TrySettleAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var outcomes = await core.Set<ScanLeg>()
            .AsNoTracking()
            .Where(leg => leg.ScanId == scanId)
            .Select(leg => leg.Outcome)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (outcomes.Any(outcome => outcome is null))
        {
            return null;
        }

        // Completed means every company in scope was reached and its answer recorded,
        // which is what the status says it means. One company that rate-limited us is a
        // run that did not cover its brokers, and reporting that as success would make the
        // status useless for the only question it is asked — did we actually look
        // everywhere. Which company, and why, is on the leg rows.
        //
        // A scan with no legs at all settles as completed: there was nobody in scope, and
        // every one of nobody was reached.
        var status = outcomes.All(outcome => ScanLegOutcomes.IsAnswer(outcome!.Value))
            ? ScanStatus.Completed
            : ScanStatus.Failed;

        var now = clock.GetUtcNow();

        // Conditional on the run still being under way, which is what makes two legs
        // arriving together harmless. It is also what stops a leg that finished after the
        // run was already settled from reopening it.
        var moved = await core.Set<Scan>()
            .Where(scan => scan.Id == scanId && scan.Status == ScanStatus.Running)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(scan => scan.Status, status)
                    .SetProperty(scan => scan.CompletedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return moved == 1 ? status : null;
    }
}
