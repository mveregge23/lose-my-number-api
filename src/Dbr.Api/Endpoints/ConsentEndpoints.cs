// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Consent;
using Dbr.Infrastructure.Consent;

namespace Dbr.Api.Endpoints;

/// <summary>
/// What the account permits: scanning for it, opening removal requests, and opening
/// them again when data reappears.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/profile</c> because that is where somebody looks for it — the switches sit
/// next to the identity they act on. They are not scoped to that profile, though:
/// consent is held by the account and covers every identity it manages. Adding a second
/// identity already takes its own explicit attestation, and asking again for the same
/// three permissions per profile would be friction bought with nothing.
/// </para>
/// <para>
/// Nothing here dispatches work or checks whether anything may run. This is the record
/// of what was decided; the checking belongs to whatever is about to act, and reads the
/// same service.
/// </para>
/// </remarks>
public static class ConsentEndpoints
{
    public static IEndpointRouteBuilder MapConsentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var consent = endpoints.MapGroup("/api/v1/profile/consent").RequireAuthorization();

        consent.MapGet("/", ListAsync);
        consent.MapPost("/", RecordAsync);

        return endpoints;
    }

    /// <summary>
    /// Where all three permissions stand, and which consent text this instance serves.
    /// </summary>
    /// <remarks>
    /// The current version is in the response because a client has to display that text
    /// and send its version back to change anything. Making it fetch the version from
    /// somewhere else would let the two drift, and the drift would only show up as a
    /// refused decision.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        IConsentService consent,
        ConsentPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var grants = await consent.ReadAsync(cancellationToken);

        return Results.Ok(new
        {
            policyVersion = policy.PolicyVersion,
            grants = grants.Select(Grant),
        });
    }

    /// <summary>Grants or withdraws one permission.</summary>
    private static async Task<IResult> RecordAsync(
        RecordConsentRequest request,
        IConsentService consent,
        CancellationToken cancellationToken)
    {
        if (ConsentRequestValidation.Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await consent.RecordAsync(
            ConsentRequestValidation.ParseScope(request.Scope)!.Value,
            request.Granted!.Value,
            request.PolicyVersion,
            cancellationToken);

        return result.Outcome switch
        {
            // A decision that changed nothing answers the same as one that did. The
            // client asked for a state and got it; which of the two happened is a fact
            // about the history, and the history is not what it asked for.
            RecordConsentOutcome.Recorded or RecordConsentOutcome.Unchanged =>
                Results.Ok(Grant(result.Grant!)),

            RecordConsentOutcome.PolicyOutOfDate => Results.Problem(
                "The consent text has been replaced since this was displayed. Fetch it again, show "
                + "the current version, and ask once more — a decision recorded against wording "
                + "nobody saw is not a record of anything.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled consent outcome {result.Outcome}."),
        };
    }

    private static object Grant(ConsentGrant grant) =>
        new
        {
            scope = ConsentRequestValidation.ToWire(grant.Scope),
            granted = grant.Granted,

            // Null for a scope nobody has decided about. An invented timestamp would
            // read as a decision to refuse, which is not what never being asked is.
            since = grant.Since,
            policyVersion = grant.PolicyVersion,
        };
}
