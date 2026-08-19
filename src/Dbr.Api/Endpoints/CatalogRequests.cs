// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Regions;

namespace Dbr.Api.Endpoints;

/// <param name="Filter">What to ask the catalog for.</param>
/// <param name="Problem">
/// Why the query string could not be turned into a filter, or <see langword="null"/> if
/// it could.
/// </param>
public sealed record BrokerFilterResult(BrokerFilter Filter, string? Problem);

/// <param name="Filter">What to ask the catalog for.</param>
/// <param name="Problem">
/// Why the query string could not be turned into a filter, or <see langword="null"/> if
/// it could.
/// </param>
public sealed record LegalBasisFilterResult(LegalBasisFilter Filter, string? Problem);

/// <summary>
/// Turns the query string on the catalog routes into a filter, or says why it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value that is not one of the values is refused, not ignored.</b> Dropping an
/// unrecognised filter would answer a different question than the one asked and look
/// like a complete list; treating it as matching nothing would look like an empty
/// catalog. Both are answers somebody would believe, which is what makes them worse than
/// an error naming the parameter.
/// </para>
/// <para>
/// An empty parameter is absent rather than invalid — <c>?removalMethod=</c> is what a
/// client sends when a form control has nothing selected, and refusing it would make
/// every such client special-case its own query string.
/// </para>
/// </remarks>
public static class CatalogFilters
{
    /// <summary>Reads the filters on <c>GET /api/v1/brokers</c>.</summary>
    public static BrokerFilterResult ParseBrokerFilter(string? removalMethod, string? legalBasisId)
    {
        RemovalMethod? method = null;

        if (Given(removalMethod))
        {
            method = CatalogVocabulary.ParseRemovalMethod(removalMethod!.Trim());

            if (method is null)
            {
                return new BrokerFilterResult(
                    default,
                    "A removal method is 'webform', 'email', 'api' or 'postal'.");
            }
        }

        Guid? basisId = null;

        if (Given(legalBasisId))
        {
            if (!Guid.TryParse(legalBasisId!.Trim(), out var parsed))
            {
                return new BrokerFilterResult(
                    default,
                    "A legal basis is identified by the id from /api/v1/legal-basis, not by its code.");
            }

            basisId = parsed;
        }

        return new BrokerFilterResult(new BrokerFilter(method, basisId), null);
    }

    /// <summary>Reads the filters on <c>GET /api/v1/legal-basis</c>.</summary>
    public static LegalBasisFilterResult ParseLegalBasisFilter(
        string? residencyScope,
        string? requestType)
    {
        string? scope = null;

        if (Given(residencyScope))
        {
            scope = RegionCode.Normalize(residencyScope);

            if (!RegionCode.IsWellFormed(scope))
            {
                // The same rule the column enforces and a profile's region is held to.
                // Saying which shape is meant is the difference between this and an
                // empty list somebody reads as "no statute covers me".
                return new LegalBasisFilterResult(
                    default,
                    "A residency scope is a coarse region code such as 'US-CA' or 'EU'.");
            }
        }

        LegalRequestType? type = null;

        if (Given(requestType))
        {
            type = CatalogVocabulary.ParseLegalRequestType(requestType!.Trim());

            if (type is null)
            {
                return new LegalBasisFilterResult(
                    default,
                    "A request type is 'delete', 'opt_out_sale' or 'opt_out_targeted_ads'.");
            }
        }

        return new LegalBasisFilterResult(new LegalBasisFilter(scope, type), null);
    }

    private static bool Given(string? value) => !string.IsNullOrWhiteSpace(value);
}
