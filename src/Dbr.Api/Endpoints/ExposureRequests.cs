// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;

namespace Dbr.Api.Endpoints;

/// <param name="Filter">What to ask for.</param>
/// <param name="Problem">
/// Why the query string could not be turned into a filter, or <see langword="null"/> if
/// it could.
/// </param>
public sealed record ExposureFilterResult(ExposureFilter Filter, string? Problem);

/// <summary>
/// Turns the query string on <c>GET /api/v1/exposures</c> into a filter, or says why it
/// cannot.
/// </summary>
/// <remarks>
/// The same stance the catalog filters take, and it matters more here. An unrecognised
/// status dropped rather than refused would answer a different question and look like a
/// complete list of somebody's exposures; treated as matching nothing it would look like
/// they have none. "You are not listed anywhere" is a sentence somebody would act on, and
/// producing it from a typo is worse than any error message.
/// </remarks>
public static class ExposureFilters
{
    /// <summary>Reads the filters on <c>GET /api/v1/exposures</c>.</summary>
    public static ExposureFilterResult Parse(string? status, string? brokerId)
    {
        ExposureStatus? parsedStatus = null;

        if (Given(status))
        {
            parsedStatus = MonitoringVocabulary.ParseExposureStatus(status!.Trim());

            if (parsedStatus is null)
            {
                return new ExposureFilterResult(
                    default,
                    "A status is 'new', 'requested', 'removed', 'reappeared' or 'dismissed'.");
            }
        }

        Guid? parsedBroker = null;

        if (Given(brokerId))
        {
            if (!Guid.TryParse(brokerId!.Trim(), out var broker))
            {
                return new ExposureFilterResult(
                    default,
                    "A broker is identified by the id from /api/v1/brokers, not by its name or domain.");
            }

            parsedBroker = broker;
        }

        return new ExposureFilterResult(new ExposureFilter(parsedStatus, parsedBroker), null);
    }

    // An empty parameter is absent rather than invalid, matching the catalog routes:
    // ?status= is what a client sends when a form control has nothing selected.
    private static bool Given(string? value) => !string.IsNullOrWhiteSpace(value);
}
