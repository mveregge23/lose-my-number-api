// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Removals;

namespace Dbr.Api.Endpoints;

/// <summary>
/// Demanding that a company act, and following what happens to the demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first thing in this API that speaks to somebody else on a person's
/// behalf.</b> Everything before it reads, searches or records; opening a demand puts a
/// person's name in front of a company in a message sent as them. That is why the consent
/// check sits on the write path here the way it does on scans, and why the two moves
/// offered below are the only two a client can make.
/// </para>
/// <para>
/// <b>A demand is about a person and a company, not about a listing.</b> The listing is
/// evidence of what prompted it and is often absent — the right to tell a company to delete
/// what it holds does not depend on having found a page with your name on it first, and an
/// opt-out of sale is prospective. The design's request body reads the other way round,
/// naming an exposure and nothing else; the schema was widened away from that and this
/// follows the schema.
/// </para>
/// </remarks>
public static class RemovalEndpoints
{
    public static IEndpointRouteBuilder MapRemovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var removals = endpoints.MapGroup("/api/v1/removal-requests").RequireAuthorization();

        removals.MapPost("/", OpenAsync);
        removals.MapGet("/", ListAsync);
        removals.MapGet("/{id:guid}", FindAsync);
        removals.MapGet("/{id:guid}/timeline", TimelineAsync);
        removals.MapPost("/{id:guid}/cancel", CancelAsync);
        removals.MapPost("/{id:guid}/retry", RetryAsync);

        return endpoints;
    }

    /// <summary>Opens a demand against one company.</summary>
    /// <remarks>
    /// Answers <c>202</c> rather than <c>201</c>, for the reason a scan does: what has been
    /// created is a demand that has been accepted and not yet sent. <c>201 Created</c> would
    /// read as the company having been asked, which is the one thing that has not happened
    /// yet — and the difference matters more here than on a scan, because somebody reading
    /// it is deciding whether their data is on its way out of a company's database.
    /// </remarks>
    private static async Task<IResult> OpenAsync(
        OpenRemovalRequest request,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        var validation = RemovalRequestValidation.Validate(request);

        if (validation.Problem is { } invalid)
        {
            return Results.Problem(invalid, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await removals.OpenAsync(
            request.ProfileId,
            request.BrokerId,
            validation.RequestType,
            request.ExposureId,
            cancellationToken);

        return result.Outcome switch
        {
            OpenRemovalOutcome.Opened => Results.Accepted(
                $"/api/v1/removal-requests/{result.Request!.Id}",
                Detail(new RemovalListing(result.Request, result.Broker!))),

            // 403 rather than 401: the caller is who they say they are, and a fresh token
            // will not help. This account has not agreed to have demands sent in its name,
            // or has withdrawn that agreement, which a client resolves by asking the person.
            OpenRemovalOutcome.ConsentMissing => Results.Problem(
                "This account has not permitted removals. Grant the 'auto_removal' consent "
                + "scope before opening one — a demand carries somebody's name to a company, "
                + "and agreeing to be searched for is not the same as agreeing to that.",
                statusCode: StatusCodes.Status403Forbidden),

            OpenRemovalOutcome.ProfileNotFound => Results.Problem(
                "No such profile. A demand is made on behalf of one of the identities this "
                + "account has already created and attested to; leave the profile out to make "
                + "it for your own.",
                statusCode: StatusCodes.Status404NotFound),

            OpenRemovalOutcome.UnknownBroker => Results.Problem(
                "This instance's catalog has no such broker. A demand is addressed to a "
                + "company this instance knows how to reach, which is what the catalog is.",
                statusCode: StatusCodes.Status400BadRequest),

            OpenRemovalOutcome.UnsupportedRemovalMethod => Results.Problem(
                "This company accepts opt-outs only by post, and this instance has no way to "
                + "send one. Refused rather than accepted and left stuck: a demand sitting "
                + "with its deadline running and nothing at the other end is worse than being "
                + "told to write a letter.",
                statusCode: StatusCodes.Status409Conflict),

            OpenRemovalOutcome.ExposureNotFound => Results.Problem(
                "No such exposure. Cite one of this account's own findings, or leave it out — "
                + "a demand that cites no listing is a complete request.",
                statusCode: StatusCodes.Status404NotFound),

            OpenRemovalOutcome.ExposureMismatch => Results.Problem(
                "That listing was found for a different identity, or on a different company, "
                + "than this demand is about. Evidence has to be about what the demand is "
                + "about — otherwise the company is sent one person's details as proof of "
                + "another person's listing.",
                statusCode: StatusCodes.Status400BadRequest),

            OpenRemovalOutcome.ExposureDismissed => Results.Problem(
                "This listing has been dismissed as not being this person, so nothing will be "
                + "sent in their name over it. Undismissing it is the way to change that, not "
                + "opening a demand around it.",
                statusCode: StatusCodes.Status409Conflict),

            OpenRemovalOutcome.AlreadyOpen => Results.Problem(
                "There is already a live demand of this kind open with this company for this "
                + "identity. Two would send the same request twice in one person's name, and "
                + "the one that exists is the row a reappearance comes back to.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled removal outcome {result.Outcome}."),
        };
    }

    /// <summary>Demands this account has opened, newest first.</summary>
    private static async Task<IResult> ListAsync(
        string? status,
        string? profileId,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        var parsed = RemovalFilters.Parse(status, profileId);

        if (parsed.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var listings = await removals.ListAsync(parsed.Filter, cancellationToken);

        return Results.Ok(new { removalRequests = listings.Select(Summary) });
    }

    /// <summary>One demand.</summary>
    private static async Task<IResult> FindAsync(
        Guid id,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        var listing = await removals.FindAsync(id, cancellationToken);

        return listing is null ? NoSuchRequest() : Results.Ok(Detail(listing));
    }

    /// <summary>What has been attempted on one demand.</summary>
    private static async Task<IResult> TimelineAsync(
        Guid id,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        var timeline = await removals.TimelineAsync(id, cancellationToken);

        if (timeline is null)
        {
            return NoSuchRequest();
        }

        return Results.Ok(new
        {
            removalRequest = Detail(new RemovalListing(timeline.Request, timeline.Broker)),

            // Named for what it is. §6.5 calls this route a state-transition history and
            // there is nowhere to read one from — nothing records that a demand moved from
            // queued to submitted, and the append-only log that will is its own story. What
            // exists is one row per attempt, which is a real history of the work done and
            // not a history of the states passed through. Serving it under the more
            // ambitious name would be the kind of gap somebody only finds by trusting it.
            attempts = timeline.Attempts.Select(Attempt),
        });
    }

    /// <summary>Calls a demand off.</summary>
    private static async Task<IResult> CancelAsync(
        Guid id,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        var result = await removals.CancelAsync(id, cancellationToken);

        return Moved(result, await Rendered(result, removals, cancellationToken));
    }

    /// <summary>Sends a failed demand back to the queue.</summary>
    private static async Task<IResult> RetryAsync(
        Guid id,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        var result = await removals.RetryAsync(id, cancellationToken);

        return Moved(result, await Rendered(result, removals, cancellationToken));
    }

    /// <summary>
    /// The one answer both moves give.
    /// </summary>
    /// <remarks>
    /// <c>409</c> for both refusals rather than <c>400</c>: the request is well formed and
    /// names something real, and what is wrong is the state that thing is in. A client
    /// that re-sent the same body after the request moved on would get the same answer,
    /// which is what makes it a conflict rather than a malformed call.
    /// </remarks>
    private static IResult Moved(MoveRemovalResult result, object? rendered) =>
        result.Outcome switch
        {
            MoveRemovalOutcome.Moved => Results.Ok(rendered),

            MoveRemovalOutcome.NotFound => NoSuchRequest(),

            MoveRemovalOutcome.NotAllowed or MoveRemovalOutcome.Refused => Results.Problem(
                result.Reason,
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled move outcome {result.Outcome}."),
        };

    /// <summary>
    /// The moved request, with the company named.
    /// </summary>
    /// <remarks>
    /// A second read, because the move works on a tracked entity and the company it is
    /// addressed to is not on it. Cheap and only on the success path — a refusal has no
    /// request to render.
    /// </remarks>
    private static async Task<object?> Rendered(
        MoveRemovalResult result,
        IRemovalService removals,
        CancellationToken cancellationToken)
    {
        if (result.Request is not { } request)
        {
            return null;
        }

        var listing = await removals.FindAsync(request.Id, cancellationToken);

        return listing is null ? null : Detail(listing);
    }

    private static object Summary(RemovalListing listing) =>
        new
        {
            id = listing.Request.Id,

            // Whose demand this is. On the row rather than reachable only through a
            // listing, which is what lets an account managing more than one identity tell
            // its own demands from a dependent's.
            profileId = listing.Request.PrivacyProfileId,

            // Null when the demand cites no listing, which is ordinary. A client showing
            // "found on" has to be able to tell that from a listing it failed to load.
            exposureId = listing.Request.ExposureId,

            requestType = CatalogVocabulary.ToWire(listing.Request.RequestType),
            status = RemovalVocabulary.ToWire(listing.Request.Status),
            strategy = RemovalVocabulary.ToWire(listing.Request.Strategy),
            attempt = listing.Request.Attempt,
            deadlineAt = listing.Request.DeadlineAt,

            // The field that says whether missing that date is disappointing or actionable.
            // Served next to it deliberately: a date without it is a number somebody will
            // read as a promise.
            deadlineSource = CatalogVocabulary.ToWire(listing.Request.DeadlineSource),

            createdAt = listing.Request.CreatedAt,
            broker = BrokerOf(listing.Broker),
        };

    private static object Detail(RemovalListing listing) =>
        new
        {
            id = listing.Request.Id,
            profileId = listing.Request.PrivacyProfileId,
            exposureId = listing.Request.ExposureId,
            requestType = CatalogVocabulary.ToWire(listing.Request.RequestType),
            status = RemovalVocabulary.ToWire(listing.Request.Status),
            strategy = RemovalVocabulary.ToWire(listing.Request.Strategy),
            attempt = listing.Request.Attempt,
            deadlineAt = listing.Request.DeadlineAt,
            deadlineSource = CatalogVocabulary.ToWire(listing.Request.DeadlineSource),

            // The regime that governed, or null when none did. Served as the id rather than
            // the citation because the citation is catalog content a client already has a
            // route for, and duplicating it here would let the two disagree.
            legalBasisId = listing.Request.LegalBasisId,

            createdAt = listing.Request.CreatedAt,
            broker = BrokerOf(listing.Broker),
        };

    private static object Attempt(RemovalJob job) =>
        new
        {
            id = job.Id,
            attemptNumber = job.AttemptNumber,

            // Which connector ran. A build-time fact rather than a catalog one, and the
            // thing somebody comparing two failures actually wants to know.
            connectorId = job.ConnectorId,

            status = RemovalVocabulary.ToWire(job.Status),
            runAt = job.RunAt,

            // Null when there is not going to be another attempt, which is the difference
            // between a demand that has stopped and one that is waiting.
            nextRetryAt = job.NextRetryAt,
        };

    /// <summary>
    /// Enough of the catalog row to name the company, and no more.
    /// </summary>
    /// <remarks>
    /// The same public fields the exposure routes publish, and deliberately not the pacing
    /// ones — how this instance decides to talk to a company is not part of what was
    /// demanded of it. A client wanting the rest has <c>/api/v1/brokers/{id}</c>.
    /// </remarks>
    private static object BrokerOf(Broker broker) =>
        new
        {
            id = broker.Id,
            name = broker.Name,
            domain = broker.Domain,
            removalMethod = CatalogVocabulary.ToWire(broker.RemovalMethod),
        };

    private static IResult NoSuchRequest() =>
        Results.Problem("No such removal request.", statusCode: StatusCodes.Status404NotFound);
}
