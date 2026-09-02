// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Dbr.Infrastructure.InternalEdge;

namespace Dbr.Api.InternalEdge;

/// <summary>
/// Recording what a leg found, from the one process that holds no keys.
/// </summary>
/// <remarks>
/// <para>
/// The second route on the internal listener, and it exists for the same reason the first
/// one does. A finding carries the address of the listing it was found on; a broker's profile
/// URL routinely spells out the name and the city of the person it is about, so it is a copy
/// of the identity rather than a reference to one and belongs in the vault. The process that
/// found it holds no keys, so it asks.
/// </para>
/// <para>
/// <b>No authorization here either, and it is not an omission.</b> Who may open a connection
/// was settled during the TLS handshake against a certificate this deployment issued; what
/// this particular call may record is carried by the grant in the body, which names one leg of
/// one scan and is spent by being used.
/// </para>
/// </remarks>
public static class FindingEndpoints
{
    public static IEndpointRouteBuilder MapFindingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/internal/v1/scans/findings", ReportAsync);

        return endpoints;
    }

    private static async Task<IResult> ReportAsync(
        ReportFindingsRequest request,
        IFindingReporter reporter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Token))
        {
            return Results.Problem(
                "A report carries the grant for the leg it belongs to. Without one there is "
                + "nothing to record it against.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var listings = new List<ReportedListing>();

        foreach (var listing in request.Listings ?? [])
        {
            if (Read(listing) is not { } read)
            {
                // Refused rather than partially recorded. A malformed report is a bug in
                // whatever built it, and recording the half of it that parsed would leave
                // somebody's findings quietly incomplete with nothing saying so.
                return Results.Problem(
                    "A reported listing names an address, a group of an identity or a degree "
                    + "of agreement that this build has no reading of.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            listings.Add(read);
        }

        var result = await reporter.ReportAsync(request.Token, listings, cancellationToken);

        if (result.Outcome is not ReportFindingsOutcome.Recorded)
        {
            // 403 and one sentence, as a refused release gives: whether a grant exists is
            // itself an answer, and this route gives the same one either way.
            return Results.Problem(
                "This grant cannot record findings. It was never issued, its window has "
                + "closed, or its findings have already been recorded.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new ReportFindingsResponse(result.Recorded, result.BelowFloor));
    }

    /// <summary>One listing as the domain has it, or nothing when it cannot be read.</summary>
    private static ReportedListing? Read(ReportedListingPayload listing)
    {
        if (!Uri.TryCreate(listing.SourceRef, UriKind.Absolute, out var source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            // The same rule the search contract keeps: a relative reference names a listing
            // only to whatever page it was read from, and anything but http is a link this
            // system would be storing and handing back without knowing what it is.
            return null;
        }

        var matches = new List<FieldMatch>();

        foreach (var match in listing.Matches ?? [])
        {
            if (IdentityVocabulary.Parse(match.Field) is not { } field
                || Strength(match.Strength) is not { } strength)
            {
                return null;
            }

            matches.Add(new FieldMatch(field, strength));
        }

        return matches.Count > 0 ? new ReportedListing(source, matches) : null;
    }

    private static MatchStrength? Strength(string? value) => value switch
    {
        "exact" => MatchStrength.Exact,
        "partial" => MatchStrength.Partial,
        "conflicting" => MatchStrength.Conflicting,
        _ => null,
    };
}
