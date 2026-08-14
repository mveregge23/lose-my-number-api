// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using Dbr.Infrastructure.Identity;
using Fido2NetLib;

namespace Dbr.Api.Endpoints;

/// <summary>
/// The passkeys an account can be reached with.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/account</c> rather than <c>/auth</c>, because everything here requires
/// already being signed in — which is the whole difference between these and the
/// routes that open an account. Adding a passkey is a thing an account does to
/// itself, and the account is never named in the request: it comes from the token.
/// </para>
/// <para>
/// An account with one passkey has one way in, and whatever holds it can be lost,
/// broken or wiped. Until there is a second, that event ends the account rather than
/// inconveniencing it — which is what these exist to fix.
/// </para>
/// </remarks>
public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var passkeys = endpoints.MapGroup("/api/v1/account/passkeys").RequireAuthorization();

        passkeys.MapGet("/", ListAsync);
        passkeys.MapPost("/options", BeginAdditionAsync);
        passkeys.MapPost("/", CompleteAdditionAsync);

        return endpoints;
    }

    /// <summary>
    /// What this account can currently be reached with.
    /// </summary>
    /// <remarks>
    /// The backup flags are the point of this list rather than decoration. They are
    /// what separates a passkey synced to a password manager from one that exists on a
    /// single device — which is exactly the difference between an account that
    /// survives a lost phone and one that does not.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        PasskeyService passkeys,
        CancellationToken cancellationToken)
    {
        var credentials = await passkeys.ListAsync(cancellationToken);

        return Results.Ok(credentials.Select(passkey => new
        {
            id = passkey.Id,
            createdAt = passkey.CreatedAt,
            lastUsedAt = passkey.LastUsedAt,
            isBackedUp = passkey.IsBackedUp,
            isBackupEligible = passkey.IsBackupEligible,
        }));
    }

    /// <summary>
    /// Starts adding a passkey to this account.
    /// </summary>
    /// <remarks>
    /// The options name the passkeys this account already has, so an authenticator
    /// that recognises one of them declines rather than creating a second credential
    /// that does the same job.
    /// </remarks>
    private static async Task<IResult> BeginAdditionAsync(
        PasskeyService passkeys,
        CancellationToken cancellationToken)
    {
        var ceremony = await passkeys.BeginAdditionAsync(cancellationToken);

        return CeremonyResponse(ceremony.CeremonyId, ceremony.Options.ToJson());
    }

    private static async Task<IResult> CompleteAdditionAsync(
        AddPasskeyRequest request,
        PasskeyService passkeys,
        CancellationToken cancellationToken)
    {
        var result = await passkeys.CompleteAdditionAsync(
            request.CeremonyId,
            request.Credential,
            cancellationToken);

        return result.Outcome switch
        {
            PasskeyAdditionOutcome.Added => Results.Ok(new { id = result.PasskeyId }),

            // A ceremony belonging to another account answers exactly as an expired or
            // invented one does. The difference is worth having in the code and worth
            // nothing to the caller, who would only learn that some handle they came
            // across was real.
            PasskeyAdditionOutcome.CeremonyUnusable or PasskeyAdditionOutcome.WrongAccount =>
                Results.Problem(
                    "That registration has expired or was already completed. Start again.",
                    statusCode: StatusCodes.Status400BadRequest),

            PasskeyAdditionOutcome.AttestationRejected => Results.Problem(
                "The authenticator's response could not be verified. If this passkey is already "
                + "registered, the authenticator will have refused to create another.",
                statusCode: StatusCodes.Status400BadRequest),

            PasskeyAdditionOutcome.AlreadyRegistered => Results.Problem(
                "That passkey is already registered.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled addition outcome {result.Outcome}."),
        };
    }

    /// <summary>
    /// Wraps a ceremony handle around options the library has already serialised —
    /// re-serialising them here would emit standard base64 where WebAuthn wants
    /// base64url, on a path that needs an authenticator to reach.
    /// </summary>
    private static IResult CeremonyResponse(Guid ceremonyId, string optionsJson) =>
        Results.Text(
            new JsonObject
            {
                ["ceremonyId"] = ceremonyId.ToString(),
                ["publicKey"] = JsonNode.Parse(optionsJson),
            }.ToJsonString(),
            "application/json");
}

/// <param name="CeremonyId">The handle returned by <c>/account/passkeys/options</c>.</param>
/// <param name="Credential">What <c>navigator.credentials.create()</c> produced.</param>
public sealed record AddPasskeyRequest(
    Guid CeremonyId,
    AuthenticatorAttestationRawResponse Credential);
