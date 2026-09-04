// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Removals;

namespace Dbr.Api.Endpoints;

/// <summary>
/// What <c>POST /api/v1/removal-requests</c> takes — and all it can take.
/// </summary>
/// <remarks>
/// <para>
/// Ids and a kind of demand, and nothing else. There is no field here for a name, an
/// address or a date of birth, the same way there is none on a scan: a demand is
/// structurally "tell company X to act on identity Y", and anything letting Y be free text
/// would let this API be used to send a company somebody else's details.
/// </para>
/// <para>
/// <b>There is no strategy field</b>, which the design sketches as an optional one that
/// falls back to the catalog. How a demand is carried out is a fact about the company —
/// one offering only a mailbox cannot be sent a form — so there is no version of that
/// choice a client should be making, and an optional field is one somebody eventually
/// sends. Unmapped members are refused across this API, so a client still sending it is
/// told rather than ignored.
/// </para>
/// <para>
/// <c>ProfileId</c> and <c>ExposureId</c> are optional and mean different things when
/// omitted. No profile is the tenant's own identity, which is the common case. No exposure
/// is a demand that cites no listing, which is ordinary rather than incomplete: the right
/// does not depend on having found anything first.
/// </para>
/// </remarks>
public sealed record OpenRemovalRequest(
    Guid BrokerId,
    string? RequestType,
    Guid? ProfileId,
    Guid? ExposureId);

/// <param name="RequestType">What is being demanded, once parsed.</param>
/// <param name="Problem">Why the body cannot be used, or <see langword="null"/> if it can.</param>
public sealed record OpenRemovalValidation(LegalRequestType RequestType, string? Problem);

/// <summary>Checks what arrives on the removal routes.</summary>
public static class RemovalRequestValidation
{
    /// <summary>The problem with this body, or the demand it asks for.</summary>
    /// <remarks>
    /// The kind of demand is required and has no default. Deletion is the obvious candidate
    /// for one and would be the wrong one to pick silently: it is the broadest of the three
    /// and the least reversible, so a client that forgot the field would be sending the
    /// strongest demand available rather than the one it meant.
    /// </remarks>
    public static OpenRemovalValidation Validate(OpenRemovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.BrokerId == Guid.Empty)
        {
            return Problem(
                "A demand needs a company to be addressed to. Use the id from "
                + "/api/v1/brokers, not the name or the domain.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestType))
        {
            return Problem(
                "A demand has to say which right is being exercised: 'delete', "
                + "'opt_out_sale' or 'opt_out_targeted_ads'. There is no default, because "
                + "the one that would read as an obvious default is also the broadest.");
        }

        if (CatalogVocabulary.ParseLegalRequestType(request.RequestType.Trim()) is not { } requestType)
        {
            return Problem(
                "A kind of demand is 'delete', 'opt_out_sale' or 'opt_out_targeted_ads'. "
                + "These are different rights with different deadlines under the same "
                + "statute, so the one asked for is recorded rather than inferred.");
        }

        if (request.ProfileId == Guid.Empty)
        {
            return Problem(
                "A profile id cannot be empty. Leave it out to make the demand on behalf of "
                + "your own identity.");
        }

        if (request.ExposureId == Guid.Empty)
        {
            return Problem(
                "An exposure id cannot be empty. Leave it out to make a demand that cites no "
                + "listing, which is a complete request rather than an incomplete one.");
        }

        return new OpenRemovalValidation(requestType, null);
    }

    private static OpenRemovalValidation Problem(string problem) => new(default, problem);
}

/// <param name="Filter">What to ask for.</param>
/// <param name="Problem">
/// Why the query string could not be turned into a filter, or <see langword="null"/> if it
/// could.
/// </param>
public sealed record RemovalFilterResult(RemovalFilter Filter, string? Problem);

/// <summary>
/// Turns the query string on <c>GET /api/v1/removal-requests</c> into a filter, or says why
/// it cannot.
/// </summary>
/// <remarks>
/// The same stance the exposure filters take: an unrecognised status is refused rather than
/// dropped or treated as matching nothing. Both of those answer a different question while
/// looking like an answer to the one asked, and here the wrong answer is "nothing has been
/// demanded on your behalf".
/// </remarks>
public static class RemovalFilters
{
    /// <summary>Reads the filters on <c>GET /api/v1/removal-requests</c>.</summary>
    public static RemovalFilterResult Parse(string? status, string? profileId)
    {
        RemovalRequestStatus? parsedStatus = null;

        if (Given(status))
        {
            parsedStatus = RemovalVocabulary.ParseRequestStatus(status!.Trim());

            if (parsedStatus is null)
            {
                return new RemovalFilterResult(
                    new RemovalFilter(null, null),
                    "A status is 'queued', 'submitted', 'requires_human_input', "
                    + "'awaiting_broker_response', 'removed', 'reappeared', 'failed', "
                    + "'expired' or 'cancelled'.");
            }
        }

        Guid? parsedProfile = null;

        if (Given(profileId))
        {
            if (!Guid.TryParse(profileId!.Trim(), out var profile))
            {
                return new RemovalFilterResult(
                    new RemovalFilter(null, null),
                    "A profile is identified by the id from /api/v1/profile, not by a name.");
            }

            parsedProfile = profile;
        }

        return new RemovalFilterResult(new RemovalFilter(parsedStatus, parsedProfile), null);
    }

    // An empty parameter is absent rather than invalid, matching the other list routes:
    // ?status= is what a client sends when a form control has nothing selected.
    private static bool Given(string? value) => !string.IsNullOrWhiteSpace(value);
}
