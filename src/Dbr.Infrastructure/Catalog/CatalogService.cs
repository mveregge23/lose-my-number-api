// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Catalog;

/// <summary>
/// The catalog as the application is allowed to see it: read, never written.
/// </summary>
/// <remarks>
/// <para>
/// Every query here is untracked. Nothing in this class can save a change — the role it
/// runs as would be refused one — so a change tracker would only be bookkeeping for edits
/// that cannot happen, over rows that are identical for every caller.
/// </para>
/// <para>
/// The filter for a regime is written as an <c>EXISTS</c> against the confirmation table
/// rather than a join. A broker confirmed against a regime twice is impossible — the pair
/// is the key — but the shape still matters: a join would have this returning a row per
/// confirmation the day a second filter is added, and the failure would be duplicate
/// brokers in a list rather than an error.
/// </para>
/// </remarks>
public sealed class CatalogService(DbrDbContext core) : ICatalogService
{
    public async Task<IReadOnlyList<Broker>> ListBrokersAsync(
        BrokerFilter filter,
        CancellationToken cancellationToken)
    {
        // Deactivated entries are left out. The listing exists to be dispatched against,
        // and an entry the operator has stopped sending traffic to is not something to
        // offer somebody as a removal they could ask for.
        var brokers = core.Set<Broker>().AsNoTracking().Where(broker => broker.Active);

        if (filter.RemovalMethod is { } method)
        {
            brokers = brokers.Where(broker => broker.RemovalMethod == method);
        }

        if (filter.LegalBasisId is { } legalBasisId)
        {
            brokers = brokers.Where(broker =>
                core.Set<BrokerLegalBasis>()
                    .Any(confirmation =>
                        confirmation.BrokerId == broker.Id
                        && confirmation.LegalBasisId == legalBasisId));
        }

        return await brokers
            // Name first because that is what a reader is scanning, id second because
            // two companies can share a name and an order that depends on which row the
            // planner reached first is not an order.
            .OrderBy(broker => broker.Name)
            .ThenBy(broker => broker.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BrokerEntry?> FindBrokerAsync(Guid brokerId, CancellationToken cancellationToken)
    {
        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == brokerId, cancellationToken)
            .ConfigureAwait(false);

        if (broker is null)
        {
            return null;
        }

        // Written here rather than as a navigation property. Nothing else walks from a
        // confirmation to the regime behind it, and a mapped relationship would be a
        // second way to express the join with no query yet asking for it.
        var regimes = await (
            from confirmation in core.Set<BrokerLegalBasis>().AsNoTracking()
            join basis in core.Set<LegalBasis>().AsNoTracking()
                on confirmation.LegalBasisId equals basis.Id
            where confirmation.BrokerId == brokerId
            orderby basis.Code, basis.ResidencyScope, basis.RequestType
            select new ConfirmedRegime(basis, confirmation.ConfirmedAt, confirmation.ConfirmedBy))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BrokerEntry(broker, regimes);
    }

    public async Task<IReadOnlyList<LegalBasis>> ListLegalBasesAsync(
        LegalBasisFilter filter,
        CancellationToken cancellationToken)
    {
        var bases = core.Set<LegalBasis>().AsNoTracking();

        if (filter.ResidencyScope is { } scope)
        {
            // An exact match, against a value the caller has already had normalized to
            // the shape the column stores. A case-insensitive comparison here would work
            // and would stop using the index that resolution depends on.
            bases = bases.Where(basis => basis.ResidencyScope == scope);
        }

        if (filter.RequestType is { } requestType)
        {
            bases = bases.Where(basis => basis.RequestType == requestType);
        }

        return await bases
            .OrderBy(basis => basis.Code)
            .ThenBy(basis => basis.ResidencyScope)
            .ThenBy(basis => basis.RequestType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LegalBasis?> FindLegalBasisAsync(
        Guid legalBasisId,
        CancellationToken cancellationToken) =>
        await core.Set<LegalBasis>()
            .AsNoTracking()
            .FirstOrDefaultAsync(basis => basis.Id == legalBasisId, cancellationToken)
            .ConfigureAwait(false);
}
