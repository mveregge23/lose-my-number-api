// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using Dbr.Infrastructure.Identity;
using Fido2NetLib;

namespace Dbr.Api.Endpoints;

/// <summary>
/// Opening an account with a passkey, and signing in with one.
/// </summary>
/// <remarks>
/// <para>
/// Each of the two flows is two requests, because a WebAuthn ceremony is two round
/// trips: <c>/options</c> issues a challenge, and the route it hangs off completes it.
/// </para>
/// <para>
/// Only signup registers a passkey today. Adding a second passkey to an account that
/// already exists needs the request to prove which account it is for, and there is no
/// way to prove that yet — tokens are not issued here. It arrives with them.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var auth = endpoints.MapGroup("/api/v1/auth");

        auth.MapPost("/register/options", BeginRegistrationAsync);
        auth.MapPost("/register", CompleteRegistrationAsync);
        auth.MapPost("/login/options", BeginLoginAsync);
        auth.MapPost("/login", CompleteLoginAsync);
        auth.MapPost("/refresh", RefreshAsync);
        auth.MapPost("/logout", LogoutAsync);

        return endpoints;
    }

    /// <summary>
    /// Starts a signup. Answers the same way whether or not the address is already
    /// registered — finding that out is what completing the ceremony is for.
    /// </summary>
    private static async Task<IResult> BeginRegistrationAsync(
        BeginRegistrationRequest request,
        PasskeyService passkeys,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Results.Problem(
                "An email address is required. It is what the passkey is labelled with in the "
                + "authenticator, and where the account is reached.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var ceremony = await passkeys.BeginSignupAsync(request.Email.Trim(), cancellationToken);

        return CeremonyResponse(ceremony.CeremonyId, ceremony.Options.ToJson());
    }

    private static async Task<IResult> CompleteRegistrationAsync(
        CompleteRegistrationRequest request,
        PasskeyService passkeys,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var result = await passkeys.CompleteSignupAsync(
            request.CeremonyId,
            request.Credential,
            cancellationToken);

        return result.Outcome switch
        {
            // Signing up signs you in. The passkey has just been used to prove the
            // account belongs to whoever registered it, so asking them to prove it
            // again immediately would be ceremony rather than security.
            PasskeySignupOutcome.Created => Results.Ok(SessionResponse(
                result.TenantId,
                await sessions.StartAsync(result.TenantId, cancellationToken))),

            PasskeySignupOutcome.CeremonyUnusable => Results.Problem(
                "That registration has expired or was already completed. Start again.",
                statusCode: StatusCodes.Status400BadRequest),

            PasskeySignupOutcome.AttestationRejected => Results.Problem(
                "The authenticator's response could not be verified.",
                statusCode: StatusCodes.Status400BadRequest),

            PasskeySignupOutcome.AddressAlreadyRegistered => Results.Problem(
                "That address already has an account. Sign in with the passkey you registered.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled signup outcome {result.Outcome}."),
        };
    }

    /// <summary>
    /// Starts a sign-in. Takes no body at all: naming an account here is exactly what
    /// this design avoids, so there is nothing to send.
    /// </summary>
    private static async Task<IResult> BeginLoginAsync(
        PasskeyService passkeys,
        CancellationToken cancellationToken)
    {
        var ceremony = await passkeys.BeginLoginAsync(cancellationToken);

        return CeremonyResponse(ceremony.CeremonyId, ceremony.Options.ToJson());
    }

    private static async Task<IResult> CompleteLoginAsync(
        CompleteLoginRequest request,
        PasskeyService passkeys,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var result = await passkeys.CompleteLoginAsync(
            request.CeremonyId,
            request.Credential,
            cancellationToken);

        // Every failure answers the same way. The distinction between "no such
        // credential" and "that signature is wrong" is only useful to somebody
        // checking whether a passkey they found belongs to an account here.
        return result.Outcome == PasskeyLoginOutcome.Authenticated
            ? Results.Ok(SessionResponse(
                result.TenantId,
                await sessions.StartAsync(result.TenantId, cancellationToken)))
            : Results.Problem("Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, spending the one presented.
    /// </summary>
    /// <remarks>
    /// The old token stops working the moment this succeeds, so a client has to keep
    /// what it gets back. That is what makes a copy of a refresh token detectable: the
    /// second party to use one finds it already spent.
    /// </remarks>
    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized();
        }

        var result = await sessions.RefreshAsync(request.RefreshToken, cancellationToken);

        // A reused token and an unknown one answer identically. The client that just
        // had its session torn down for reuse is not told that is what happened —
        // being told would be most useful to whoever caused it.
        return result is { Outcome: SessionRefreshOutcome.Renewed, Session: { } session }
            ? Results.Ok(SessionResponse(tenantId: null, session))
            : Unauthorized();
    }

    /// <summary>
    /// Ends the session a refresh token belongs to.
    /// </summary>
    /// <remarks>
    /// Always answers the same way, whether or not the token was real — otherwise this
    /// becomes a way to ask whether a token found somewhere is still worth something.
    /// <para>
    /// Access tokens already issued keep working until they expire. Nothing checks a
    /// session on an ordinary request, which is what makes ordinary requests cheap;
    /// the price is that signing out ends the session but not the access token in
    /// flight, and the access token's lifetime is how long that lasts.
    /// </para>
    /// </remarks>
    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await sessions.SignOutAsync(request.RefreshToken, cancellationToken);
        }

        return Results.NoContent();
    }

    private static IResult Unauthorized() =>
        Results.Problem("Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);

    private static object SessionResponse(Guid? tenantId, IssuedSession session) =>
        new
        {
            tenantId,
            accessToken = session.AccessToken,
            accessTokenExpiresAt = session.AccessTokenExpiresAt,
            refreshToken = session.RefreshToken,
            refreshTokenExpiresAt = session.RefreshTokenExpiresAt,
        };

    /// <summary>
    /// Wraps a ceremony handle around options the library has already serialised.
    /// </summary>
    /// <remarks>
    /// Re-serialising the options object here would break it. WebAuthn puts raw bytes
    /// on the wire as base64url, and the library's own contracts know that where the
    /// default serialiser would emit standard base64 — which a browser rejects, on a
    /// path that needs an authenticator to reach.
    /// </remarks>
    private static IResult CeremonyResponse(Guid ceremonyId, string optionsJson) =>
        Results.Text(
            new JsonObject
            {
                ["ceremonyId"] = ceremonyId.ToString(),
                ["publicKey"] = JsonNode.Parse(optionsJson),
            }.ToJsonString(),
            "application/json");
}

/// <param name="Email">Where the account is reached, and how the passkey is labelled.</param>
public sealed record BeginRegistrationRequest(string Email);

/// <param name="CeremonyId">The handle returned by <c>/register/options</c>.</param>
/// <param name="Credential">What <c>navigator.credentials.create()</c> produced.</param>
public sealed record CompleteRegistrationRequest(
    Guid CeremonyId,
    AuthenticatorAttestationRawResponse Credential);

/// <param name="CeremonyId">The handle returned by <c>/login/options</c>.</param>
/// <param name="Credential">What <c>navigator.credentials.get()</c> produced.</param>
public sealed record CompleteLoginRequest(
    Guid CeremonyId,
    AuthenticatorAssertionRawResponse Credential);

/// <param name="RefreshToken">The refresh token from the most recent response.</param>
public sealed record RefreshRequest(string RefreshToken);
