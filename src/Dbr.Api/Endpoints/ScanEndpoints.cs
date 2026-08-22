// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;

namespace Dbr.Api.Endpoints;

/// <summary>
/// Asking for a scan, and reading the ones already asked for.
/// </summary>
/// <remarks>
/// <para>
/// What a scan <i>found</i> is not here. Exposures are their own collection, because a
/// finding outlives the run that produced it: it gets a removal request opened against
/// it, goes away, comes back, and is still the same finding. Reaching them only through
/// the scan that first saw them would make the history of one listing a walk across
/// several runs.
/// </para>
/// <para>
/// These routes require a token, unlike the catalog's. Everything here belongs to an
/// account — which identities it manages, when it looked, what was found — and there is
/// no version of that question that can be answered without knowing whose it is.
/// </para>
/// </remarks>
public static class ScanEndpoints
{
    public static IEndpointRouteBuilder MapScanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var scans = endpoints.MapGroup("/api/v1/scans").RequireAuthorization();

        scans.MapPost("/", RequestAsync);
        scans.MapGet("/", ListAsync);
        scans.MapGet("/{id:guid}", FindAsync);

        return endpoints;
    }

    /// <summary>Asks for a scan of one of the tenant's own identities.</summary>
    /// <remarks>
    /// Answers <c>202</c> rather than <c>201</c>. A scan is a run that has been accepted
    /// and has not happened — the resource exists to be watched, and <c>201 Created</c>
    /// would suggest the thing asked for is done.
    /// </remarks>
    private static async Task<IResult> RequestAsync(
        RequestScanRequest request,
        IScanService scans,
        CancellationToken cancellationToken)
    {
        if (ScanRequestValidation.Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await scans.RequestAsync(request.ProfileId, request.BrokerIds, cancellationToken);

        return result.Outcome switch
        {
            RequestScanOutcome.Queued => Results.Accepted(
                $"/api/v1/scans/{result.Scan!.Id}",
                Detail(new ScanDetail(result.Scan, request.BrokerIds ?? []))),

            // 403 rather than 401: the caller is who they say they are, and the answer is
            // not that they should authenticate again. It is that this account has not
            // agreed to be searched for, or has withdrawn that agreement — which a client
            // resolves by asking the person, not by getting a fresh token.
            RequestScanOutcome.ConsentMissing => Results.Problem(
                "This account has not permitted scanning. Grant the 'scan' consent scope before "
                + "asking for one — a search nobody agreed to is not something to run and "
                + "apologise for later.",
                statusCode: StatusCodes.Status403Forbidden),

            RequestScanOutcome.ProfileNotFound => Results.Problem(
                "No such profile. A scan searches for one of the identities this account has "
                + "already created and attested to; leave the profile out to search for your own.",
                statusCode: StatusCodes.Status404NotFound),

            RequestScanOutcome.UnknownBroker => Results.Problem(
                "This instance's catalog has no broker with "
                + $"{(result.UnknownBrokerIds.Count == 1 ? "id" : "ids")} "
                + $"{string.Join(", ", result.UnknownBrokerIds)}. Narrowing is refused rather "
                + "than trimmed to what exists: a scan over fewer brokers than were asked for, "
                + "reported as the scan that was asked for, is a smaller answer that looks "
                + "complete.",
                statusCode: StatusCodes.Status400BadRequest),

            _ => throw new InvalidOperationException($"Unhandled scan outcome {result.Outcome}."),
        };
    }

    /// <summary>Every scan this account has asked for, newest first.</summary>
    private static async Task<IResult> ListAsync(
        IScanService scans,
        CancellationToken cancellationToken)
    {
        var history = await scans.ListAsync(cancellationToken);

        // No pagination, deliberately, and for a different reason than the catalog's: this
        // list grows by one a month per identity under the scheduled cadence, so a tenant
        // reaches a page's worth after years rather than at any particular size. The answer
        // when that changes is a cursor here, not a cap that silently truncates history.
        return Results.Ok(new { scans = history.Select(Summary) });
    }

    /// <summary>One scan, with the brokers it was narrowed to.</summary>
    private static async Task<IResult> FindAsync(
        Guid id,
        IScanService scans,
        CancellationToken cancellationToken)
    {
        var detail = await scans.FindAsync(id, cancellationToken);

        return detail is null
            ? Results.Problem("No such scan.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(Detail(detail));
    }

    private static object Summary(Scan scan) =>
        new
        {
            id = scan.Id,
            profileId = scan.PrivacyProfileId,
            trigger = MonitoringVocabulary.ToWire(scan.Trigger),
            status = MonitoringVocabulary.ToWire(scan.Status),
            requestedAt = scan.RequestedAt,

            // Null while the run has not reached these, rather than absent or zeroed. A
            // client rendering "queued for 20 minutes" needs to be able to tell a run that
            // has not started from one that started at the epoch.
            startedAt = scan.StartedAt,
            completedAt = scan.CompletedAt,
        };

    private static object Detail(ScanDetail detail) =>
        new
        {
            id = detail.Scan.Id,
            profileId = detail.Scan.PrivacyProfileId,
            trigger = MonitoringVocabulary.ToWire(detail.Scan.Trigger),
            status = MonitoringVocabulary.ToWire(detail.Scan.Status),
            requestedAt = detail.Scan.RequestedAt,
            startedAt = detail.Scan.StartedAt,
            completedAt = detail.Scan.CompletedAt,

            // Empty means the whole catalog, which is what the request meant when it left
            // the list out. Serving it as an empty list rather than omitting the field
            // keeps the response the same shape either way; what empty means is the one
            // thing a client has to be told, and it is documented on the endpoint.
            brokerIds = detail.BrokerIds,
        };
}
