// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Consent;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Records scan requests and reads back what has been asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>It resolves the profile without decrypting it.</b> Working out which identity a
/// scan is for needs the profile's id and whether it belongs to this tenant, both of
/// which are core-store columns. Going through the profile service instead would put the
/// vault and the key manager on the path of queueing a scan, and every request-serving
/// process would then hold the ability to decrypt in order to do something that never
/// needs plaintext.
/// </para>
/// <para>
/// <b>Consent is checked here rather than at the endpoint.</b> Scheduled scans will
/// arrive through this same method without an HTTP request in front of them, and a check
/// that lives in a route handler is one the scheduler would have to remember to repeat.
/// </para>
/// <para>
/// <b>It takes no tenant and reads none.</b> The scan's owner is the owner of the profile
/// it is for, which the query filter has already established. Reading the ambient tenant
/// separately would introduce a second answer to whose scan this is, and two answers is
/// one more than a boundary can have.
/// </para>
/// </remarks>
public sealed class ScanService(DbrDbContext core, IConsentService consent) : IScanService
{
    public async Task<RequestScanResult> RequestAsync(
        Guid? profileId,
        IReadOnlyList<Guid>? brokerIds,
        CancellationToken cancellationToken)
    {
        if (!await consent.IsGrantedAsync(ConsentScope.Scan, cancellationToken).ConfigureAwait(false))
        {
            // Before anything else, including before finding out whether the profile
            // exists. An account that has not permitted scanning should not be able to
            // learn which profile ids are real by watching which errors come back.
            return RequestScanResult.Failed(RequestScanOutcome.ConsentMissing);
        }

        var profile = await FindProfileAsync(profileId, cancellationToken).ConfigureAwait(false);

        if (profile is null)
        {
            return RequestScanResult.Failed(RequestScanOutcome.ProfileNotFound);
        }

        // Deduplicated before checking, so that naming a broker twice is not an error and
        // does not produce two rows the primary key would reject anyway.
        var narrowing = (brokerIds ?? []).Distinct().ToList();
        var unknown = await UnknownBrokersAsync(narrowing, cancellationToken).ConfigureAwait(false);

        if (unknown.Count > 0)
        {
            return RequestScanResult.Unknown(unknown);
        }

        var scan = new Scan
        {
            Id = Guid.NewGuid(),
            TenantId = profile.TenantId,
            PrivacyProfileId = profile.Id,
            Trigger = ScanTrigger.Manual,
            Status = ScanStatus.Queued,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        core.Set<Scan>().Add(scan);

        foreach (var brokerId in narrowing)
        {
            core.Set<ScanBroker>().Add(new ScanBroker
            {
                TenantId = scan.TenantId,
                ScanId = scan.Id,
                BrokerId = brokerId,
            });
        }

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Queued and left there. The lane that would pick it up is per-broker and does
        // not exist yet; enqueueing onto a transport nobody has chosen would mean
        // settling the message shape against an imagined consumer.
        return RequestScanResult.Queued(scan);
    }

    public async Task<IReadOnlyList<Scan>> ListAsync(CancellationToken cancellationToken) =>
        await core.Set<Scan>()
            .AsNoTracking()
            .OrderByDescending(scan => scan.RequestedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<ScanDetail?> FindAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var scan = await core.Set<Scan>()
            .AsNoTracking()
            .FirstOrDefaultAsync(scan => scan.Id == scanId, cancellationToken)
            .ConfigureAwait(false);

        if (scan is null)
        {
            // Somebody else's scan and a scan that was never created answer the same,
            // because telling them apart would confirm that an id is in use elsewhere.
            return null;
        }

        var brokerIds = await core.Set<ScanBroker>()
            .AsNoTracking()
            .Where(narrowing => narrowing.ScanId == scanId)
            .Select(narrowing => narrowing.BrokerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ScanDetail(scan, brokerIds);
    }

    /// <summary>
    /// The profile a scan is for: the one named, or the tenant's own when none was.
    /// </summary>
    /// <remarks>
    /// Both paths read through the tenant query filter, so a profile belonging to another
    /// account is simply not found. The database says the same thing a second time when
    /// the row is written — the scan's foreign key is over the tenant and the profile
    /// together — which is what makes this a lookup rather than the check the guarantee
    /// rests on.
    /// </remarks>
    private async Task<PrivacyProfile?> FindProfileAsync(
        Guid? profileId,
        CancellationToken cancellationToken)
    {
        var profiles = core.Set<PrivacyProfile>().AsNoTracking();

        return profileId is { } id
            ? await profiles
                .FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken)
                .ConfigureAwait(false)
            : await profiles
                .FirstOrDefaultAsync(
                    profile => profile.RelationshipType == ProfileRelationship.Self,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Which of the named brokers this instance's catalog does not have.
    /// </summary>
    /// <remarks>
    /// Inactive brokers are not among them. A row marked inactive is still a company this
    /// instance knows about, and whether it gets contacted is decided when the scan runs
    /// — refusing the request instead would fail a scan that was perfectly valid when it
    /// was asked for, because a catalog row changed in between.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> UnknownBrokersAsync(
        IReadOnlyList<Guid> brokerIds,
        CancellationToken cancellationToken)
    {
        if (brokerIds.Count == 0)
        {
            return [];
        }

        var known = await core.Set<Broker>()
            .AsNoTracking()
            .Where(broker => brokerIds.Contains(broker.Id))
            .Select(broker => broker.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. brokerIds.Except(known)];
    }
}
