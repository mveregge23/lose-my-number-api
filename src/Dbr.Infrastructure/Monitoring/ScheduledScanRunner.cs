// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Consent;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Writes one account's recurring scans, as that account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consent is checked here too, and it is not a formality.</b> A scheduled scan is the
/// case the check exists for: somebody who granted scanning a year ago and withdrew it
/// last week is not searched for this month, and nobody has to remember to stop the
/// schedule. It reads the same service the endpoint does, so there is one answer to
/// whether scanning is permitted rather than two that agree until they do not.
/// </para>
/// <para>
/// <b>Every identity the account manages, not only its own.</b> A dependent's profile was
/// created deliberately, under an explicit attestation, for the purpose of having their
/// data removed too — monitoring only the <c>self</c> profile would mean the tenant had
/// to remember to scan for their own child by hand every month.
/// </para>
/// </remarks>
public sealed class ScheduledScanRunner(DbrDbContext core, IConsentService consent) : IScheduledScanRunner
{
    public async Task<ScheduledScanRun> RunAsync(CancellationToken cancellationToken)
    {
        if (!await consent.IsGrantedAsync(ConsentScope.Scan, cancellationToken).ConfigureAwait(false))
        {
            return ScheduledScanRun.Refused();
        }

        var profiles = await core.Set<PrivacyProfile>()
            .AsNoTracking()
            .Select(profile => new { profile.Id, profile.TenantId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The same boundary the unique index uses, so the check and the constraint agree
        // on what "today" is regardless of where the process happens to be running.
        var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        var alreadyDone = await core.Set<Scan>()
            .AsNoTracking()
            .Where(scan => scan.Trigger == ScanTrigger.Scheduled && scan.RequestedAt >= dayStart)
            .Select(scan => scan.PrivacyProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var done = alreadyDone.ToHashSet();
        var queued = 0;
        var skipped = done.Count;

        foreach (var profile in profiles.Where(profile => !done.Contains(profile.Id)))
        {
            var scan = new Scan
            {
                Id = Guid.NewGuid(),
                TenantId = profile.TenantId,
                PrivacyProfileId = profile.Id,
                Trigger = ScanTrigger.Scheduled,
                Status = ScanStatus.Queued,
                RequestedAt = DateTimeOffset.UtcNow,

                // Not narrowed. A recurring scan is the whole catalog by definition —
                // narrowing is something a person does for one run, and a subset chosen
                // once would silently become the subset monitored forever.
            };

            core.Set<Scan>().Add(scan);

            try
            {
                await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                queued++;
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another scheduler got there between the read above and this write. That
                // is the case the index exists for, and it is not a problem — the scan it
                // wrote is the scan this one was about to write. Saved one at a time
                // rather than in a batch precisely so that one collision does not roll
                // back the identities that did go through.
                core.Entry(scan).State = EntityState.Detached;
                skipped++;
            }
        }

        return new ScheduledScanRun(queued, skipped, false);
    }
}
