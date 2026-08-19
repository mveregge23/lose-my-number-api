// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Regions;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Catalog;

/// <summary>
/// The intersection: where somebody lives, against what a broker has been confirmed
/// subject to, for the kind of request being made.
/// </summary>
/// <remarks>
/// <para>
/// Untracked reads over shared rows, like everything else that touches the catalog. This
/// computes and returns; it writes nothing, because the row the answer belongs on does
/// not exist yet.
/// </para>
/// <para>
/// The candidates are resolved to dates in memory rather than picked in SQL. That is not
/// laziness about the query: a business-day count cannot be compared against a calendar
/// one without turning both into dates first, and the database has no notion of a
/// weekend. A broker has a handful of confirmed regimes at most, so the set being ordered
/// here is tiny.
/// </para>
/// </remarks>
public sealed class JurisdictionResolver(DbrDbContext core) : IJurisdictionResolver
{
    public async Task<DeadlineResolution> ResolveAsync(
        Guid brokerId,
        string? residencyRegion,
        LegalRequestType requestType,
        DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == brokerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No broker {brokerId} in the catalog. A deadline resolved against a company this "
                + "instance has never heard of would be a date with nothing behind it.");

        var governing = await GoverningAsync(brokerId, residencyRegion, requestType, cancellationToken)
            .ConfigureAwait(false);

        if (governing.Count == 0)
        {
            // Nobody has confirmed a statute reaches this company for this kind of
            // request, so what applies is the company's own target — and it is labelled
            // as that rather than dressed up as a legal deadline. The broker's target is
            // a plain count of days; no statute is involved, so no unit question arises.
            return new DeadlineResolution(
                DeadlineCalculator.Add(from, broker.SlaDays, DeadlineUnit.Calendar),
                DeadlineSource.OperationalDefault,
                LegalBasisId: null);
        }

        // The shortest window wins, and "shortest" is a date rather than a count.
        // Fifteen business days and forty-five calendar days cannot be compared as
        // integers — the smaller number is the longer window often enough that doing so
        // would quietly pick the wrong statute, and the wrong statute is recorded on the
        // request as the one that governed it.
        var earliest = governing
            .Select(basis => new
            {
                Basis = basis,
                At = DeadlineCalculator.Add(from, basis.ResponseDeadlineDays, basis.DeadlineUnit),
            })
            .OrderBy(candidate => candidate.At)

            // Two regimes can land on the same day, and which one is recorded as having
            // governed should not depend on the order rows came back in.
            .ThenBy(candidate => candidate.Basis.Code, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Basis.Id)
            .First();

        return new DeadlineResolution(earliest.At, DeadlineSource.Statutory, earliest.Basis.Id);
    }

    /// <summary>
    /// The regimes confirmed to reach this broker that also protect this region and grant
    /// this kind of request.
    /// </summary>
    /// <remarks>
    /// All three conditions are the intersection, including the request type. A regime
    /// that gives somebody forty-five days to have data deleted says nothing about how
    /// long an opt-out may take, and the catalog keys them separately for that reason.
    /// </remarks>
    private async Task<List<LegalBasis>> GoverningAsync(
        Guid brokerId,
        string? residencyRegion,
        LegalRequestType requestType,
        CancellationToken cancellationToken)
    {
        var region = RegionCode.Normalize(residencyRegion);

        if (region is null)
        {
            // Somebody who has not said where they live has no jurisdiction to intersect
            // against. That resolves to the broker's own target, which is the same answer
            // a resident of a state with no statute gets — and the honest one, since the
            // alternative is guessing at a residency in order to promise a legal deadline.
            return [];
        }

        return await (
            from confirmation in core.Set<BrokerLegalBasis>().AsNoTracking()
            join basis in core.Set<LegalBasis>().AsNoTracking()
                on confirmation.LegalBasisId equals basis.Id
            where confirmation.BrokerId == brokerId
                && basis.ResidencyScope == region
                && basis.RequestType == requestType
            select basis)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
