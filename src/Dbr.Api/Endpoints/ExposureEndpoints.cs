// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Monitoring;

namespace Dbr.Api.Endpoints;

/// <summary>
/// What the scans found, and the one judgement only the person can make about it.
/// </summary>
/// <remarks>
/// <para>
/// A collection of their own rather than a branch of the scan that produced them,
/// because a finding outlives its run: a removal request is opened against it, it goes
/// away, it comes back, and it is still the same listing. Reaching them only through
/// scans would make the history of one exposure a walk across several of them.
/// </para>
/// <para>
/// <b>These carry the closest thing to a match this API serves.</b> §6.6 draws the line
/// deliberately: a notification says a listing was found on a named broker, and seeing
/// anything about the match itself takes an authenticated call to the detail route here.
/// Today that distinction costs nothing, because a finding has no matched fields to
/// serve — the pointer to the broker's page is restricted-tier and lives nowhere yet. It
/// is worth stating anyway, since this is the route those fields will land on.
/// </para>
/// </remarks>
public static class ExposureEndpoints
{
    public static IEndpointRouteBuilder MapExposureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var exposures = endpoints.MapGroup("/api/v1/exposures").RequireAuthorization();

        exposures.MapGet("/", ListAsync);
        exposures.MapGet("/{id:guid}", FindAsync);
        exposures.MapPost("/{id:guid}/dismiss", DismissAsync);

        return endpoints;
    }

    /// <summary>Findings for this account, newest first.</summary>
    /// <remarks>
    /// Not paginated, and for a firmer reason than the catalog's. A listing that comes
    /// back after removal reappears on the row that already knows its history rather than
    /// as a new row, so this table holds roughly one finding per broker per identity — it
    /// is bounded by the size of the catalog rather than growing with time. A cursor here
    /// would be machinery for growth that does not happen.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        string? status,
        string? brokerId,
        IExposureService exposures,
        CancellationToken cancellationToken)
    {
        var parsed = ExposureFilters.Parse(status, brokerId);

        if (parsed.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var findings = await exposures.ListAsync(parsed.Filter, cancellationToken);

        return Results.Ok(new { exposures = findings.Select(Listing) });
    }

    /// <summary>One finding.</summary>
    private static async Task<IResult> FindAsync(
        Guid id,
        IExposureService exposures,
        CancellationToken cancellationToken)
    {
        var listing = await exposures.FindAsync(id, cancellationToken);

        return listing is null ? NoSuchExposure() : Results.Ok(Listing(listing));
    }

    /// <summary>Says a finding is not this person.</summary>
    private static async Task<IResult> DismissAsync(
        Guid id,
        IExposureService exposures,
        CancellationToken cancellationToken)
    {
        var result = await exposures.DismissAsync(id, cancellationToken);

        return result.Outcome switch
        {
            // Dismissing twice answers the same as dismissing once. The client asked for
            // a state and the state holds; which call put it there is a fact about the
            // history rather than about the answer.
            DismissExposureOutcome.Dismissed or DismissExposureOutcome.AlreadyDismissed =>
                Results.Ok(Listing(result.Listing!)),

            DismissExposureOutcome.NotFound => NoSuchExposure(),

            DismissExposureOutcome.RemovalInFlight => Results.Problem(
                "A removal request is open against this listing, so it cannot be dismissed. "
                + "Saying it is not you while a request is in flight in your name over it would "
                + "leave the contradiction standing at the broker rather than resolving it — "
                + "cancel the request first.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled dismiss outcome {result.Outcome}."),
        };
    }

    private static object Listing(ExposureListing listing) =>
        new
        {
            id = listing.Exposure.Id,
            scanId = listing.Exposure.ScanId,

            // Whose listing this is. On the row rather than reachable only through the
            // scan, which matters for an account managing more than one identity: a list
            // mixing a person's own findings with their dependent's, and no way to tell
            // them apart, is a list nobody can act on.
            profileId = listing.Exposure.PrivacyProfileId,
            status = MonitoringVocabulary.ToWire(listing.Exposure.Status),

            // A ranking aid rather than a claim. The tenant is the only one who can say
            // whether a listing is actually them, which is what the dismiss route is.
            confidence = listing.Exposure.Confidence,

            discoveredAt = listing.Exposure.DiscoveredAt,

            // Null when nothing has looked again since it was found, which is a different
            // thing from confirmed-present-recently and worth telling apart from outside.
            lastVerifiedAt = listing.Exposure.LastVerifiedAt,

            broker = Broker(listing.Broker),
        };

    /// <summary>
    /// Enough of the catalog row to name the company, and no more.
    /// </summary>
    /// <remarks>
    /// The same public fields the catalog routes publish, and deliberately not the pacing
    /// ones — how this instance decides to talk to a broker is not part of what somebody
    /// was found on. A client wanting the rest already has <c>/api/v1/brokers/{id}</c>.
    /// </remarks>
    private static object Broker(Broker broker) =>
        new
        {
            id = broker.Id,
            name = broker.Name,
            domain = broker.Domain,
            removalMethod = CatalogVocabulary.ToWire(broker.RemovalMethod),
        };

    private static IResult NoSuchExposure() =>
        Results.Problem("No such exposure.", statusCode: StatusCodes.Status404NotFound);
}
