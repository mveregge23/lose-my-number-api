// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Reads findings, joined to the catalog rows that say whose site they are on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The join crosses the tenant boundary in the one direction that is allowed.</b>
/// Exposures are scoped and brokers are not, so this is a filtered set joined to an
/// unfiltered one. That works because the catalog belongs to nobody — the same property
/// that lets the public routes serve it without a token — and it is worth naming because
/// the reverse, a scoped table joined into a shared answer, is the mistake that would
/// leak.
/// </para>
/// <para>
/// <b>Nothing here writes an exposure.</b> Findings arrive from a scan, and no scan runs
/// yet; this reads what will be there and lets the tenant disown a row. Dismissal is the
/// only write, and it is the only state change in this table a person makes rather than a
/// worker.
/// </para>
/// </remarks>
public sealed class ExposureService(DbrDbContext core) : IExposureService
{
    public async Task<IReadOnlyList<ExposureListing>> ListAsync(
        ExposureFilter filter,
        CancellationToken cancellationToken)
    {
        var exposures = core.Set<Exposure>().AsNoTracking();

        if (filter.Status is { } status)
        {
            exposures = exposures.Where(exposure => exposure.Status == status);
        }

        if (filter.BrokerId is { } brokerId)
        {
            exposures = exposures.Where(exposure => exposure.BrokerId == brokerId);
        }

        // Ordered before the projection, not after. Sorting on a field reached through a
        // constructed ExposureListing is something EF cannot turn into SQL, and it fails
        // at the first call rather than at compile time.
        return await (
            from exposure in exposures
            join broker in Brokers() on exposure.BrokerId equals broker.Id
            orderby exposure.DiscoveredAt descending, exposure.Id
            select new ExposureListing(exposure, broker))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExposureListing?> FindAsync(Guid exposureId, CancellationToken cancellationToken) =>
        await (
            from exposure in core.Set<Exposure>().AsNoTracking()
            where exposure.Id == exposureId
            join broker in Brokers() on exposure.BrokerId equals broker.Id
            select new ExposureListing(exposure, broker))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<DismissExposureResult> DismissAsync(
        Guid exposureId,
        CancellationToken cancellationToken)
    {
        // Tracked rather than not: this one is going to be written. The query filter still
        // applies, so somebody else's finding is not found rather than found and refused.
        var exposure = await core.Set<Exposure>()
            .FirstOrDefaultAsync(exposure => exposure.Id == exposureId, cancellationToken)
            .ConfigureAwait(false);

        if (exposure is null)
        {
            return DismissExposureResult.Failed(DismissExposureOutcome.NotFound);
        }

        if (exposure.Status == ExposureStatus.Requested)
        {
            // Not reachable until removal requests exist, and written now because the
            // alternative is that whoever builds them inherits a dismiss that silently
            // contradicts an in-flight request.
            return DismissExposureResult.Failed(DismissExposureOutcome.RemovalInFlight);
        }

        if (exposure.Status == ExposureStatus.Dismissed)
        {
            return new DismissExposureResult(
                DismissExposureOutcome.AlreadyDismissed,
                await ListingFor(exposure, cancellationToken).ConfigureAwait(false));
        }

        exposure.Status = ExposureStatus.Dismissed;
        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DismissExposureResult(
            DismissExposureOutcome.Dismissed,
            await ListingFor(exposure, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The catalog rows a finding is joined to.
    /// </summary>
    /// <remarks>
    /// Joined inner rather than left. A finding's <c>broker_id</c> is a foreign key to a
    /// table nothing deletes from, so a finding with no broker is not a state to render
    /// gracefully — it is a broken database, and quietly dropping the row would hide that
    /// while making somebody's list incomplete without saying so.
    /// </remarks>
    private IQueryable<Broker> Brokers() => core.Set<Broker>().AsNoTracking();

    private async Task<ExposureListing> ListingFor(Exposure exposure, CancellationToken cancellationToken)
    {
        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstAsync(broker => broker.Id == exposure.BrokerId, cancellationToken)
            .ConfigureAwait(false);

        return new ExposureListing(exposure, broker);
    }
}
