// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// What a finished run says about the demands that were waiting on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A removal is confirmed by looking, not by a clock.</b> A company that says it deleted
/// somebody's data and a company that says nothing are the same evidence, which is none —
/// so the only thing that closes a demand is a later search of the same company finding
/// nothing. That is what this reads: a run has finished, and each of its legs is an answer
/// about one company.
/// </para>
/// <para>
/// <b>Any run counts, not only one asked for on purpose.</b> A scheduled monthly run looks
/// at the same companies for the same person, so its answer is exactly as good as one
/// commissioned to check. Requiring a special kind of run would mean ignoring evidence
/// already gathered and asking a company the same question twice.
/// </para>
/// <para>
/// <b>The two directions are not symmetrical, and that is deliberate.</b> A listing found
/// gone is good news whenever it arrives, so it settles the demand immediately — there is
/// nothing to wait for once the thing asked for has happened. A listing still present is
/// only a failure <i>after</i> the deadline: before it, the company is still within the time
/// it is allowed, and recording that as a failure would hold it to a deadline that had not
/// arrived.
/// </para>
/// <para>
/// What this makes "failed" mean is worth stating, because it is the point of the story.
/// Before this, a demand nobody answered would have sat in <c>AwaitingBrokerResponse</c>
/// for ever — nothing drove it anywhere. Now the terminal state a silent company earns is
/// reached by <i>going and looking</i>, so <b>failed means "we checked, and you are still
/// listed"</b> rather than "a timer expired". That is the difference between a status a
/// person can act on and one that only records our own impatience.
/// </para>
/// </remarks>
public sealed class RemovalVerification(
    DbrDbContext core,
    TimeProvider clock,
    ILogger<RemovalVerification> logger)
{
    /// <summary>
    /// Settles every demand this run answered, and leaves the rest alone.
    /// </summary>
    /// <returns>How many demands reached a terminal state because of it.</returns>
    public async Task<int> ResolveAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var scan = await core.Set<Scan>()
            .AsNoTracking()
            .Where(row => row.Id == scanId)
            .Select(row => new { row.PrivacyProfileId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (scan is null)
        {
            return 0;
        }

        // Only the legs that are an answer about the company. A leg that was rate-limited or
        // met a changed page says nothing about whether somebody is listed, and treating
        // silence as absence would confirm a removal that never happened — the one mistake
        // here that a person would act on.
        var answers = await core.Set<ScanLeg>()
            .AsNoTracking()
            .Where(leg => leg.ScanId == scanId
                && (leg.Outcome == ScanLegOutcome.Found || leg.Outcome == ScanLegOutcome.NothingFound))
            .Select(leg => new { leg.BrokerId, leg.Outcome, leg.CandidatesRecorded })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (answers.Count == 0)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        var settled = 0;

        foreach (var answer in answers)
        {
            // Nothing kept is the same as nothing found. A run that saw candidates and
            // recorded none of them found nobody who was actually this person — the floor
            // is what decides that, and it decided it once already.
            var clear = answer.Outcome == ScanLegOutcome.NothingFound || answer.CandidatesRecorded == 0;

            var waiting = await core.Set<RemovalRequest>()
                .Where(request => request.PrivacyProfileId == scan.PrivacyProfileId
                    && request.BrokerId == answer.BrokerId
                    && request.Status == RemovalRequestStatus.AwaitingBrokerResponse)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var request in waiting)
            {
                if (clear)
                {
                    Move(request, RemovalRequestStatus.Removed);
                    settled++;

                    logger.LogInformation(
                        "Removal request {RequestId} is confirmed: run {ScanId} found nothing for "
                        + "this person at broker {BrokerId}.",
                        request.Id,
                        scanId,
                        answer.BrokerId);

                    continue;
                }

                if (now < request.DeadlineAt)
                {
                    // Still listed, and still within the time the company is allowed. Not an
                    // answer yet, and recording one would hold them to a deadline that has
                    // not arrived.
                    continue;
                }

                Move(request, RemovalRequestStatus.Failed);
                settled++;

                logger.LogInformation(
                    "Removal request {RequestId} passed its deadline and run {ScanId} still finds "
                    + "this person at broker {BrokerId}.",
                    request.Id,
                    scanId,
                    answer.BrokerId);
            }

            if (clear)
            {
                await ConfirmExposuresAsync(
                    scan.PrivacyProfileId,
                    answer.BrokerId,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return settled;
    }

    /// <summary>
    /// The findings this answer is about, marked as gone.
    /// </summary>
    /// <remarks>
    /// The demand is what a person asked for and the finding is what they were shown, and
    /// leaving the second saying "requested" after the first has been confirmed would leave
    /// two answers to the same question on one screen. Terminal findings are not touched: a
    /// dismissed one was a false positive and is not news, and one already removed is
    /// already right.
    /// </remarks>
    private async Task ConfirmExposuresAsync(
        Guid profileId,
        Guid brokerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var findings = await core.Set<Exposure>()
            .Where(exposure => exposure.PrivacyProfileId == profileId
                && exposure.BrokerId == brokerId
                && (exposure.Status == ExposureStatus.Requested
                    || exposure.Status == ExposureStatus.Reappeared))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var finding in findings)
        {
            finding.Status = ExposureStatus.Removed;
            finding.LastVerifiedAt = now;
        }
    }

    /// <summary>
    /// Moves a demand, refusing a move the lifecycle does not draw.
    /// </summary>
    /// <remarks>
    /// The same guard the work handler keeps, for the same reason: every transition in this
    /// system goes through one table, so a state reached by a route nobody drew is a bug
    /// found here rather than a row nobody can explain later.
    /// </remarks>
    private static void Move(RemovalRequest request, RemovalRequestStatus to)
    {
        if (!RemovalLifecycle.IsAllowed(request.Status, to))
        {
            throw new InvalidOperationException(
                $"A finished run would move removal request {request.Id} from {request.Status} "
                + $"to {to}, which the lifecycle does not allow: "
                + RemovalLifecycle.Refuse(request.Status, to));
        }

        request.Status = to;
    }
}
